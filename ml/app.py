import os
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from threading import RLock
from typing import Any

import joblib
import numpy as np
from fastapi import FastAPI
from pydantic import BaseModel, Field
from sklearn.ensemble import IsolationForest

MODEL_PATH = Path(os.getenv("MODEL_PATH", "/data/petcore_presence_iforest.joblib"))
MODEL: IsolationForest | None = None
MODEL_LOCK = RLock()
MODEL_VERSION = "2.0.0"
MIN_WINDOWS = 30

@asynccontextmanager
async def lifespan(_: FastAPI):
    load_model()
    yield

app = FastAPI(title="PetCore Food AI — ML", version="1.0.0", lifespan=lifespan)

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
    history: list[Point] = Field(default_factory=list)

class TrainRequest(BaseModel):
    history: list[Point] = Field(default_factory=list)

def utc_datetime(value: datetime) -> datetime:
    """Interpret naive timestamps as UTC and normalize aware timestamps to UTC."""
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)

def load_model() -> IsolationForest | None:
    global MODEL
    with MODEL_LOCK:
        if MODEL is not None:
            return MODEL
        if not MODEL_PATH.exists():
            return None
        try:
            stored = joblib.load(MODEL_PATH)
            if isinstance(stored, IsolationForest) and getattr(stored, "n_features_in_", None) == 11:
                MODEL = stored
            else:
                MODEL = None
        except Exception:
            MODEL = None
        return MODEL

def save_model(model: IsolationForest) -> None:
    MODEL_PATH.parent.mkdir(parents=True, exist_ok=True)
    temporary_path = MODEL_PATH.with_name(f"{MODEL_PATH.name}.tmp")
    joblib.dump(model, temporary_path)
    os.replace(temporary_path, MODEL_PATH)

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
    valid_topics = {"petcore/status", "petcore/comida", "petcore/horario"}
    ordered_points = sorted(points, key=lambda p: utc_datetime(p.at))
    for point in ordered_points:
        if point.topic not in valid_topics:
            continue
        # Use UTC epoch-minute consistently regardless of container locale or timestamp offset.
        minute = int(utc_datetime(point.at).timestamp() // 60)
        buckets.setdefault(minute, []).append(point)
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
            snapshots.append((minute, pet, food, clock_minute if clock_minute is not None else float(minute % 1440), last_pet_change if last_pet_change is not None else minute, last_food_change if last_food_change is not None else minute))

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
    load_model()
    return {"status": "ok", "model": "IsolationForest", "trained": MODEL is not None, "version": MODEL_VERSION if MODEL else None, "minimumWindows": MIN_WINDOWS, "window": "1 minute"}

@app.post("/analyze")
def analyze(req: AnalyzeRequest) -> dict[str, Any]:
    global MODEL
    pts = [*req.history, Point(id=req.measurementId, topic=req.topic, value=req.value, at=req.at)]
    x = features(pts)
    if len(x) < MIN_WINDOWS:
        return {"measurementId": req.measurementId, "result": "coletando", "score": None, "model": "IsolationForest", "version": None, "analyzedAt": datetime.now(timezone.utc), "explanation": f"Coletando histórico: {len(x)} de {MIN_WINDOWS} janelas de um minuto com estados válidos de presença e comida."}
    row = x[-1].reshape(1, -1)
    with MODEL_LOCK:
        if MODEL is None:
            load_model()
        if MODEL is None:
            # Fit once on the baseline. Keep the newest row for classification when there is a prior baseline.
            model = IsolationForest(n_estimators=100, contamination=0.05, random_state=42)
            model.fit(x[:-1] if len(x) > MIN_WINDOWS else x)
            save_model(model)
            MODEL = model
        score = float(MODEL.decision_function(row)[0])
        prediction = int(MODEL.predict(row)[0])
    result = "anomalia" if prediction == -1 else "normal"
    reason = "Combinação ou duração dos estados de presença e comida fora da distribuição histórica capturada pelo modelo." if result == "anomalia" else "Combinação dos estados de presença e comida compatível com a distribuição histórica capturada pelo modelo."
    return {"measurementId": req.measurementId, "result": result, "score": score, "model": "IsolationForest", "version": MODEL_VERSION, "parameters": {"n_estimators": 100, "contamination": 0.05, "random_state": 42, "window": "1 minute"}, "analyzedAt": datetime.now(timezone.utc), "explanation": reason}

@app.post("/train")
def train(req: TrainRequest) -> dict[str, Any]:
    global MODEL
    with MODEL_LOCK:
        if MODEL is None:
            load_model()
        if MODEL is not None:
            samples = getattr(MODEL, "n_samples_fit_", None)
            return {"status": "ja_treinado", "model": "IsolationForest", "version": MODEL_VERSION, "samples": samples, "parameters": {"n_estimators": 100, "contamination": 0.05, "random_state": 42}}

    x = features(req.history)
    if len(x) < MIN_WINDOWS:
        return {"status": "coletando_dados", "windows": len(x), "minimumWindows": MIN_WINDOWS, "message": "Ainda são necessárias janelas válidas de presença e comida para treinar o modelo."}
    with MODEL_LOCK:
        if MODEL is None:
            load_model()
        if MODEL is not None:
            samples = getattr(MODEL, "n_samples_fit_", None)
            return {"status": "ja_treinado", "model": "IsolationForest", "version": MODEL_VERSION, "samples": samples, "parameters": {"n_estimators": 100, "contamination": 0.05, "random_state": 42}}
        model = IsolationForest(n_estimators=100, contamination=0.05, random_state=42)
        model.fit(x)
        save_model(model)
        MODEL = model
    return {"status": "treinado", "model": "IsolationForest", "version": MODEL_VERSION, "samples": len(x), "trainedAt": datetime.now(timezone.utc), "parameters": {"n_estimators": 100, "contamination": 0.05, "random_state": 42}}
