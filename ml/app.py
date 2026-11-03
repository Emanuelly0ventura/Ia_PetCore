import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import joblib
import numpy as np
from fastapi import FastAPI
from pydantic import BaseModel
from sklearn.ensemble import IsolationForest

app = FastAPI(title="PetCore Food AI — ML", version="1.0.0")
MODEL_PATH = Path(os.getenv("MODEL_PATH", "/data/petcore_presence_iforest.joblib"))
MODEL: IsolationForest | None = None
MODEL_VERSION = "2.0.0"
MIN_WINDOWS = 30

class Point(BaseModel):
    id: int | None = None
    topic: str
    value: str
    at: datetime

class AnalyzeRequest(BaseModel):
    measurementId: int
    deviceId: str
    topic: str
    value: str
    at: datetime
    history: list[Point] = []

class TrainRequest(BaseModel):
    history: list[Point] = []

def presence(value: str, topic: str) -> float | None:
    normalized = value.strip().upper()
    if topic == "petcore/status":
        if normalized == "PET DETECTADO": return 1.0
        if normalized == "SEM PRESENCA": return 0.0
    if topic == "petcore/comida":
        if normalized == "COM COMIDA": return 1.0
        if normalized == "SEM COMIDA": return 0.0
    return None

def features(points: list[Point]) -> np.ndarray:
    # Collapse the 2-second MQTT stream into one snapshot per occupied minute.
    # Repeated identical reads in the same minute do not become independent samples.
    buckets: dict[int, list[Point]] = {}
    for point in sorted(points, key=lambda p: p.at):
        buckets.setdefault(int(point.at.timestamp() // 60), []).append(point)
    if not buckets: return np.empty((0, 11))

    pet = food = None
    clock_minute: float | None = None
    last_pet_change: int | None = None
    last_food_change: int | None = None
    snapshots: list[tuple[int, float, float, float, int, int]] = []
    for minute in sorted(buckets):
        for p in buckets[minute]:
            value = presence(p.value, p.topic)
            if p.topic == "petcore/status" and value is not None:
                if value != pet: pet, last_pet_change = value, minute
            elif p.topic == "petcore/comida" and value is not None:
                if value != food: food, last_food_change = value, minute
            elif p.topic == "petcore/horario":
                try:
                    hour, mins, _ = map(int, p.value.split(":"))
                    if 0 <= hour < 24 and 0 <= mins < 60: clock_minute = hour * 60 + mins
                except (ValueError, AttributeError):
                    pass
        if pet is not None and food is not None:
            snapshots.append((minute, pet, food, clock_minute if clock_minute is not None else float(minute % 1440), last_pet_change or minute, last_food_change or minute))

    rows: list[list[float]] = []
    for i, (minute, pet_now, food_now, clock, pet_change_at, food_change_at) in enumerate(snapshots):
        window = snapshots[max(0, i - 14):i + 1]
        prev_pet, prev_food = snapshots[i - 1][1:3] if i else (pet_now, food_now)
        prev_minute = snapshots[i - 1][0] if i else minute
        angle = 2 * np.pi * clock / 1440
        rows.append([
            pet_now, food_now, pet_now - prev_pet, food_now - prev_food,
            float(np.mean([x[1] for x in window])), float(np.mean([x[2] for x in window])),
            float(minute - pet_change_at), float(minute - food_change_at),
            float(min(max(minute - prev_minute, 0), 1440)), float(np.sin(angle)), float(np.cos(angle)),
        ])
    return np.asarray(rows, dtype=float) if rows else np.empty((0, 11))

@app.get("/health")
def health() -> dict[str, Any]:
    global MODEL
    if MODEL is None and MODEL_PATH.exists():
        try:
            stored = joblib.load(MODEL_PATH)
            if getattr(stored, "n_features_in_", None) == 11: MODEL = stored
        except Exception:
            MODEL = None
    return {"status": "ok", "model": "IsolationForest", "trained": MODEL is not None, "version": MODEL_VERSION if MODEL else None, "minimumWindows": MIN_WINDOWS, "window": "1 minute"}

@app.post("/analyze")
def analyze(req: AnalyzeRequest) -> dict[str, Any]:
    global MODEL
    pts = [*req.history, Point(id=req.measurementId, topic=req.topic, value=req.value, at=req.at)]
    x = features(pts)
    if len(x) < MIN_WINDOWS:
        return {"measurementId": req.measurementId, "result": "coletando", "score": None, "model": "IsolationForest", "version": None, "analyzedAt": datetime.now(timezone.utc), "explanation": f"Coletando histórico: {len(x)} de {MIN_WINDOWS} janelas de um minuto com estados válidos de presença e comida."}
    if MODEL is None and MODEL_PATH.exists():
        try:
            stored = joblib.load(MODEL_PATH)
            if getattr(stored, "n_features_in_", None) == 11: MODEL = stored
        except Exception:
            MODEL = None
    row = x[-1].reshape(1, -1)
    if MODEL is None:
        # Train only on the first historical baseline; defer the current point so it can be assessed against prior context.
        MODEL = IsolationForest(n_estimators=100, contamination=0.05, random_state=42)
        MODEL.fit(x[:-1] if len(x) > MIN_WINDOWS else x)
        MODEL_PATH.parent.mkdir(parents=True, exist_ok=True)
        joblib.dump(MODEL, MODEL_PATH)
    score = float(MODEL.decision_function(row)[0])
    result = "anomalia" if int(MODEL.predict(row)[0]) == -1 else "normal"
    reason = "Combinação ou duração dos estados de presença e comida fora da distribuição histórica capturada pelo modelo." if result == "anomalia" else "Combinação dos estados de presença e comida compatível com a distribuição histórica capturada pelo modelo."
    return {"measurementId": req.measurementId, "result": result, "score": score, "model": "IsolationForest", "version": MODEL_VERSION, "parameters": {"n_estimators": 100, "contamination": 0.05, "random_state": 42, "window": "1 minute"}, "analyzedAt": datetime.now(timezone.utc), "explanation": reason}

@app.post("/train")
def train(req: TrainRequest) -> dict[str, Any]:
    global MODEL
    x = features(req.history)
    if len(x) < MIN_WINDOWS:
        return {"status": "coletando_dados", "windows": len(x), "minimumWindows": MIN_WINDOWS, "message": "Ainda são necessárias janelas válidas de presença e comida para treinar o modelo."}
    MODEL = IsolationForest(n_estimators=100, contamination=0.05, random_state=42)
    MODEL.fit(x)
    MODEL_PATH.parent.mkdir(parents=True, exist_ok=True)
    joblib.dump(MODEL, MODEL_PATH)
    return {"status": "treinado", "model": "IsolationForest", "version": MODEL_VERSION, "samples": len(x), "trainedAt": datetime.now(timezone.utc), "parameters": {"n_estimators": 100, "contamination": 0.05, "random_state": 42}}
