var PotDetail = (function() {
  var voltageChart = null;
  var currentChart = null;

  var chartDefaults = {
    responsive: true,
    maintainAspectRatio: false,
    animation: { duration: 300 },
    scales: {
      x: {
        ticks: { color: '#a0a0c0', maxTicksLimit: 8, font: { size: 10 } },
        grid: { color: 'rgba(15,52,96,0.5)' }
      },
      y: {
        ticks: { color: '#a0a0c0', font: { size: 10 } },
        grid: { color: 'rgba(15,52,96,0.5)' }
      }
    },
    plugins: {
      legend: { display: false }
    }
  };

  function openModal(cell) {
    var overlay = document.getElementById('modalOverlay');
    overlay.classList.add('open');

    var zone = cell.row < 5 ? 'A区' : 'B区';
    document.getElementById('modalTitle').textContent = cell.name + ' 详情';
    document.getElementById('modalInfoRow').innerHTML =
      '<div><span class="info-label">槽号:</span><span class="info-val">' + cell.id + '</span></div>' +
      '<div><span class="info-label">名称:</span><span class="info-val">' + cell.name + '</span></div>' +
      '<div><span class="info-label">区域:</span><span class="info-val">' + zone + '</span></div>' +
      '<div><span class="info-label">状态:</span><span class="info-val">' + cell.status + '</span></div>';

    fetchTrendData(cell.id);
    document.getElementById('feedTableBody').innerHTML = '<tr><td colspan="3" style="text-align:center;color:#a0a0c0;">加载中...</td></tr>';
  }

  function closeModal() {
    document.getElementById('modalOverlay').classList.remove('open');
    if (voltageChart) { voltageChart.destroy(); voltageChart = null; }
    if (currentChart) { currentChart.destroy(); currentChart = null; }
  }

  function fetchTrendData(cellId) {
    fetch('/api/celldata/' + cellId + '/trend')
      .then(function(r) { return r.json(); })
      .then(function(data) {
        var voltageCurrentData = data.voltageCurrentData || [];
        var voltageTrend = voltageCurrentData.map(function(p) {
          return { time: p.receivedAt, value: p.voltage };
        });
        var currentTrend = voltageCurrentData.map(function(p) {
          return { time: p.receivedAt, value: p.current };
        });
        var feedRecords = (data.feedingRecords || []).map(function(f) {
          return { time: f.fedAt, type: f.feedType, amount: f.feedAmountKg };
        });
        renderVoltageChart(voltageTrend);
        renderCurrentChart(currentTrend);
        renderFeedTable(feedRecords);
      })
      .catch(function() {
        renderVoltageChart(generateMockTrend(8, 3.8, 4.5, 'V'));
        renderCurrentChart(generateMockTrend(8, 300, 360, 'kA'));
        renderFeedTable(generateMockFeeds());
      });
  }

  function renderVoltageChart(trend) {
    if (voltageChart) { voltageChart.destroy(); voltageChart = null; }
    var labels = trend.map(function(p) {
      var d = new Date(p.time);
      return d.getHours().toString().padStart(2, '0') + ':' + d.getMinutes().toString().padStart(2, '0');
    });
    var values = trend.map(function(p) { return p.value; });
    voltageChart = new Chart(document.getElementById('voltageChart'), {
      type: 'line',
      data: {
        labels: labels,
        datasets: [{
          data: values,
          borderColor: '#4caf50',
          backgroundColor: 'rgba(76,175,80,0.1)',
          borderWidth: 2,
          fill: true,
          tension: 0.3,
          pointRadius: 0,
          pointHoverRadius: 4
        }]
      },
      options: JSON.parse(JSON.stringify(chartDefaults))
    });
  }

  function renderCurrentChart(trend) {
    if (currentChart) { currentChart.destroy(); currentChart = null; }
    var labels = trend.map(function(p) {
      var d = new Date(p.time);
      return d.getHours().toString().padStart(2, '0') + ':' + d.getMinutes().toString().padStart(2, '0');
    });
    var values = trend.map(function(p) { return p.value; });
    currentChart = new Chart(document.getElementById('currentChart'), {
      type: 'line',
      data: {
        labels: labels,
        datasets: [{
          data: values,
          borderColor: '#64b5f6',
          backgroundColor: 'rgba(100,181,246,0.1)',
          borderWidth: 2,
          fill: true,
          tension: 0.3,
          pointRadius: 0,
          pointHoverRadius: 4
        }]
      },
      options: JSON.parse(JSON.stringify(chartDefaults))
    });
  }

  function renderFeedTable(records) {
    var tbody = document.getElementById('feedTableBody');
    if (!records.length) {
      tbody.innerHTML = '<tr><td colspan="3" style="text-align:center;color:#a0a0c0;">暂无记录</td></tr>';
      return;
    }
    var html = '';
    for (var i = 0; i < records.length; i++) {
      var r = records[i];
      var d = new Date(r.time);
      var ts = d.getHours().toString().padStart(2, '0') + ':' + d.getMinutes().toString().padStart(2, '0') + ':' + d.getSeconds().toString().padStart(2, '0');
      html += '<tr><td>' + ts + '</td><td>' + r.type + '</td><td>' + r.amount + '</td></tr>';
    }
    tbody.innerHTML = html;
  }

  function generateMockTrend(hours, min, max, unit) {
    var points = [];
    var now = Date.now();
    for (var i = hours * 6; i >= 0; i--) {
      points.push({
        time: new Date(now - i * 10 * 60000).toISOString(),
        value: min + Math.random() * (max - min)
      });
    }
    return points;
  }

  function generateMockFeeds() {
    var types = ['氧化铝', '氟化盐', '氧化铝'];
    var feeds = [];
    var now = Date.now();
    for (var i = 0; i < 10; i++) {
      feeds.push({
        time: new Date(now - i * 3600000 * Math.random() * 3).toISOString(),
        type: types[i % 3],
        amount: (15 + Math.random() * 25).toFixed(1)
      });
    }
    return feeds;
  }

  function init() {
    document.getElementById('modalCloseBtn').addEventListener('click', closeModal);
    document.getElementById('modalOverlay').addEventListener('click', function(e) {
      if (e.target === this) closeModal();
    });
  }

  return {
    init: init,
    openModal: openModal,
    closeModal: closeModal
  };
})();
