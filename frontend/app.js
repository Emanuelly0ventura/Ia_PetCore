const API = localStorage.getItem('petcoreApi') || 'http://localhost:5080';
let chart;
async function get(path) {
  const response = await fetch(API + path);
  if (!response.ok) throw Error(String(response.status));
  return response.json();
}
function fmt(value) {
  return value ? new Date(value).toLocaleString('pt-BR', { hour:'2-digit', minute:'2-digit', day:'2-digit', month:'2-digit' }) : '—';
}
function esc(value) {
  const element = document.createElement('span');
  element.textContent = value == null ? '' : String(value);
  return element.innerHTML;
}
function draw(items) {
  const status = [...items].filter(x => x.sensor === 'petcore/status').reverse();
  const food = [...items].filter(x => x.sensor === 'petcore/comida').reverse();
  document.getElementById('empty').style.display = items.length ? 'none' : 'grid';
  const labels = [...new Set(status.concat(food).map(x => fmt(x.receivedAt)))].sort();
  const petByTime = new Map(status.map(x => [fmt(x.receivedAt), x.value === 'PET DETECTADO' ? 1 : 0]));
  const foodByTime = new Map(food.map(x => [fmt(x.receivedAt), x.value === 'COM COMIDA' ? 1 : 0]));
  const data = { labels, datasets: [
    { label:'Presença do pet', data:labels.map(x => petByTime.has(x) ? petByTime.get(x) : null), borderColor:'#16866e', backgroundColor:'rgba(22,134,110,.08)', stepped:true, spanGaps:false, pointRadius:2 },
    { label:'Presença de comida', data:labels.map(x => foodByTime.has(x) ? foodByTime.get(x) : null), borderColor:'#d49b37', backgroundColor:'rgba(212,155,55,.06)', stepped:true, spanGaps:false, pointRadius:2 }
  ] };
  if (chart) { chart.data = data; chart.update('none'); return; }
  chart = new Chart(document.getElementById('chart'), { type:'line', data, options:{ responsive:true, maintainAspectRatio:false, plugins:{legend:{display:true, position:'bottom'}}, scales:{ y:{min:-.1,max:1.1,ticks:{stepSize:1,callback:v=>v===1?'Presente':v===0?'Ausente':''},grid:{color:'#eef2ef'}}, x:{grid:{display:false},ticks:{maxTicksLimit:7}} } } });
}
async function refresh() {
  try {
    const [dash, measurements, analyses, alerts] = await Promise.all([
      get('/api/dashboard'), get('/api/measurements?limit=120'), get('/api/analyses?limit=20'), get('/api/alerts')
    ]);
    document.querySelector('.live').classList.add('ok');
    document.getElementById('apiState').textContent = 'API conectada';
    document.getElementById('total').textContent = dash.measurements;
    document.getElementById('alerts').textContent = dash.alertsOpen;
    document.getElementById('pet').textContent = dash.petState || 'Sem leitura';
    document.getElementById('latestAt').textContent = dash.petAt ? 'Recebido ' + fmt(dash.petAt) : 'Aguardando estado do pet';
    document.getElementById('food').textContent = dash.foodState || 'Sem leitura';
    document.getElementById('foodAt').textContent = dash.foodAt ? 'Recebido ' + fmt(dash.foodAt) : 'Aguardando estado da comida';
    const online = dash.lastSeenAt && Date.now() - new Date(dash.lastSeenAt) < 15000;
    document.getElementById('device').textContent = dash.deviceStatus || 'Sem comunicação';
    document.getElementById('seen').textContent = online ? 'Visto ' + fmt(dash.lastSeenAt) : 'Sem comunicação recente';
    draw(measurements);
    const latest = measurements.filter(x => x.sensor === 'petcore/status' || x.sensor === 'petcore/comida' || x.sensor === 'petcore/horario').slice(0, 12);
    document.getElementById('rows').innerHTML = latest.map(x => '<tr><td>' + esc(x.sensor.replace('petcore/','')) + '</td><td><span class="pill ' + ((x.value === 'SEM PRESENCA' || x.value === 'SEM COMIDA') ? 'off' : '') + '">' + esc(x.value) + '</span></td><td>' + esc(x.device) + '</td><td>' + fmt(x.receivedAt) + '</td></tr>').join('') || '<tr><td colspan="4" class="muted">Nenhuma medição recebida ainda.</td></tr>';
    const latestAnalysis = analyses.find(x => x.result === 'normal' || x.result === 'anomalia');
    document.getElementById('model').textContent = latestAnalysis
      ? (latestAnalysis.result === 'anomalia' ? 'Padrão conjunto fora do histórico' : 'Padrão conjunto dentro do histórico') + ' · score ' + (latestAnalysis.score == null ? '—' : latestAnalysis.score.toFixed(4)) + ' · ' + fmt(latestAnalysis.analyzedAt)
      : 'Coletando janelas de um minuto com estados de presença e comida.';
    document.getElementById('alertList').innerHTML = alerts.slice(0, 4).map(a => '<div class="alertitem">' + esc(a.title) + ' · ' + fmt(a.createdAt) + '</div>').join('');
  } catch (error) {
    document.querySelector('.live').classList.remove('ok');
    document.getElementById('apiState').textContent = 'API indisponível';
    document.getElementById('model').textContent = 'Não foi possível consultar a API. Verifique o PostgreSQL e o backend.';
  }
}
refresh();
setInterval(refresh, 3000);
