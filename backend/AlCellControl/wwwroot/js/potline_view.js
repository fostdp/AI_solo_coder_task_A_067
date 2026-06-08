var PotlineView = (function() {
  var ROWS = 10, COLS = 20;
  var cells = [];
  var hoveredCell = null;
  var flashPhase = 0;
  var flashTimer = 0;
  var canvas, ctx, tooltip;
  var onCellClick = null;

  function init(canvasEl, tooltipEl, clickCallback) {
    canvas = canvasEl;
    ctx = canvas.getContext('2d');
    tooltip = tooltipEl;
    onCellClick = clickCallback;

    for (var r = 0; r < ROWS; r++) {
      for (var c = 0; c < COLS; c++) {
        cells.push({
          id: r * COLS + c + 1,
          row: r,
          col: c,
          voltage: 4.0 + Math.random() * 0.5,
          concentration: 1.8 + Math.random() * 1.0,
          effectProb: Math.random() * 30,
          status: '运行中',
          name: (r < 5 ? 'A' : 'B') + '区-' + (r * COLS + c + 1) + '号槽'
        });
      }
    }

    canvas.addEventListener('mousemove', function(e) {
      var rect = canvas.getBoundingClientRect();
      var mx = e.clientX - rect.left;
      var my = e.clientY - rect.top;
      var cell = hitTest(mx, my);
      hoveredCell = cell;
      if (cell) {
        canvas.style.cursor = 'pointer';
        tooltip.style.display = 'block';
        tooltip.innerHTML =
          '<div><span class="tt-label">槽号: </span><span class="tt-val">' + cell.id + '</span></div>' +
          '<div><span class="tt-label">电压: </span><span class="tt-val">' + cell.voltage.toFixed(2) + ' V</span></div>' +
          '<div><span class="tt-label">浓度: </span><span class="tt-val">' + cell.concentration.toFixed(2) + '%</span></div>' +
          '<div><span class="tt-label">效应概率: </span><span class="tt-val">' + cell.effectProb.toFixed(1) + '%</span></div>';
        var tx = e.clientX - rect.left + 16;
        var ty = e.clientY - rect.top - 10;
        if (tx + 180 > rect.width) tx = e.clientX - rect.left - 180;
        if (ty + 100 > rect.height) ty = e.clientY - rect.top - 100;
        tooltip.style.left = tx + 'px';
        tooltip.style.top = ty + 'px';
      } else {
        canvas.style.cursor = 'default';
        tooltip.style.display = 'none';
      }
    });

    canvas.addEventListener('click', function(e) {
      var rect = canvas.getBoundingClientRect();
      var mx = e.clientX - rect.left;
      var my = e.clientY - rect.top;
      var cell = hitTest(mx, my);
      if (cell && onCellClick) onCellClick(cell);
    });

    canvas.addEventListener('mouseleave', function() {
      hoveredCell = null;
      tooltip.style.display = 'none';
    });
  }

  function getCellColor(cell) {
    if (cell.effectProb > 80 && flashPhase) return 'rgba(244,67,54,0.2)';
    if (cell.concentration < 1.5) return '#f44336';
    if (cell.concentration < 2.0) return '#ff9800';
    return '#4caf50';
  }

  function drawRoundedRect(ctx, x, y, w, h, r) {
    r = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + w - r, y);
    ctx.quadraticCurveTo(x + w, y, x + w, y + r);
    ctx.lineTo(x + w, y + h - r);
    ctx.quadraticCurveTo(x + w, y + h, x + w - r, y + h);
    ctx.lineTo(x + r, y + h);
    ctx.quadraticCurveTo(x, y + h, x, y + h - r);
    ctx.lineTo(x, y + r);
    ctx.quadraticCurveTo(x, y, x + r, y);
    ctx.closePath();
  }

  function drawCanvas() {
    var W = canvas.width / window.devicePixelRatio;
    var H = canvas.height / window.devicePixelRatio;
    ctx.clearRect(0, 0, W, H);

    var leftMargin = 36;
    var topMargin = 28;
    var bottomMargin = 24;
    var rightMargin = 12;
    var zoneGap = 20;

    var availW = W - leftMargin - rightMargin;
    var availH = H - topMargin - bottomMargin - zoneGap;

    var gapX = 3, gapY = 3;
    var cw = (availW - (COLS - 1) * gapX) / COLS;
    var ch = (availH - (ROWS - 1) * gapY) / ROWS;

    cw = Math.max(cw, 8);
    ch = Math.max(ch, 8);

    var totalGridW = COLS * cw + (COLS - 1) * gapX;
    var totalGridH = ROWS * ch + (ROWS - 1) * gapY + zoneGap;
    var offsetX = leftMargin + (availW - totalGridW) / 2;
    var offsetY = topMargin + (availH - totalGridH) / 2;

    ctx.font = '11px Microsoft YaHei, SimHei, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (var c = 0; c < COLS; c++) {
      ctx.fillStyle = '#a0a0c0';
      ctx.fillText(c + 1, offsetX + c * (cw + gapX) + cw / 2, offsetY - 12);
    }
    ctx.textAlign = 'right';
    for (var r = 0; r < ROWS; r++) {
      var ry = offsetY + r * (ch + gapY);
      if (r >= 5) ry += zoneGap;
      ctx.fillStyle = '#a0a0c0';
      ctx.fillText(r + 1, offsetX - 8, ry + ch / 2);
    }

    var zoneCenterY1 = offsetY + 4 * (ch + gapY) + ch + zoneGap / 2;
    ctx.save();
    ctx.font = 'bold 13px Microsoft YaHei, SimHei, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = '#64b5f6';
    ctx.fillText('A区', offsetX - 20, offsetY + 2 * (ch + gapY) + ch);
    ctx.fillStyle = '#ce93d8';
    ctx.fillText('B区', offsetX - 20, offsetY + 7 * (ch + gapY) + zoneGap + ch);

    ctx.strokeStyle = 'rgba(100,181,246,0.3)';
    ctx.setLineDash([6, 4]);
    ctx.beginPath();
    ctx.moveTo(offsetX, zoneCenterY1);
    ctx.lineTo(offsetX + totalGridW, zoneCenterY1);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.restore();

    for (var i = 0; i < cells.length; i++) {
      var cell = cells[i];
      var cr = cell.row, cc = cell.col;
      var cry = offsetY + cr * (ch + gapY);
      if (cr >= 5) cry += zoneGap;
      var cx = offsetX + cc * (cw + gapX);

      var color = getCellColor(cell);
      drawRoundedRect(ctx, cx, cry, cw, ch, 3);
      ctx.fillStyle = color;
      ctx.fill();

      if (hoveredCell && hoveredCell.id === cell.id) {
        drawRoundedRect(ctx, cx, cry, cw, ch, 3);
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 2;
        ctx.stroke();
      }

      ctx.fillStyle = '#fff';
      ctx.font = cw > 30 ? '10px Microsoft YaHei, SimHei, sans-serif' : '8px Microsoft YaHei, SimHei, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(cell.id, cx + cw / 2, cry + ch / 2);
    }
  }

  function hitTest(mx, my) {
    var W = canvas.width / window.devicePixelRatio;
    var H = canvas.height / window.devicePixelRatio;

    var leftMargin = 36, topMargin = 28, bottomMargin = 24, rightMargin = 12, zoneGap = 20;
    var availW = W - leftMargin - rightMargin;
    var availH = H - topMargin - bottomMargin - zoneGap;
    var gapX = 3, gapY = 3;
    var cw = (availW - (COLS - 1) * gapX) / COLS;
    var ch = (availH - (ROWS - 1) * gapY) / ROWS;
    cw = Math.max(cw, 8); ch = Math.max(ch, 8);
    var totalGridW = COLS * cw + (COLS - 1) * gapX;
    var totalGridH = ROWS * ch + (ROWS - 1) * gapY + zoneGap;
    var offsetX = leftMargin + (availW - totalGridW) / 2;
    var offsetY = topMargin + (availH - totalGridH) / 2;

    for (var i = 0; i < cells.length; i++) {
      var cell = cells[i];
      var ry = offsetY + cell.row * (ch + gapY);
      if (cell.row >= 5) ry += zoneGap;
      var cx = offsetX + cell.col * (cw + gapX);
      if (mx >= cx && mx <= cx + cw && my >= ry && my <= ry + ch) return cell;
    }
    return null;
  }

  function updateCells(overviewData) {
    if (!overviewData || !Array.isArray(overviewData)) return;
    for (var i = 0; i < overviewData.length; i++) {
      var d = overviewData[i];
      var idx = d.cellId - 1;
      if (idx >= 0 && idx < cells.length) {
        cells[idx].voltage = d.voltage !== undefined ? d.voltage : cells[idx].voltage;
        cells[idx].concentration = d.aluminaConcentration !== undefined ? d.aluminaConcentration : cells[idx].concentration;
        cells[idx].effectProb = d.anodeEffectProbability !== undefined ? d.anodeEffectProbability * 100 : cells[idx].effectProb;
        cells[idx].name = d.cellName || cells[idx].name;
        if (d.latestAlarmType) {
          cells[idx].status = d.latestAlarmLevel === 2 ? '阳极效应预警' : '浓度告警';
        } else {
          cells[idx].status = '运行中';
        }
      }
    }
  }

  function resizeCanvas() {
    var wrap = document.getElementById('canvasWrap');
    canvas.width = wrap.clientWidth * window.devicePixelRatio;
    canvas.height = wrap.clientHeight * window.devicePixelRatio;
    canvas.style.width = wrap.clientWidth + 'px';
    canvas.style.height = wrap.clientHeight + 'px';
    ctx.setTransform(window.devicePixelRatio, 0, 0, window.devicePixelRatio, 0, 0);
    drawCanvas();
  }

  function startAnimation() {
    function animate(timestamp) {
      if (timestamp - flashTimer >= 500) {
        flashPhase = !flashPhase;
        flashTimer = timestamp;
      }
      drawCanvas();
      requestAnimationFrame(animate);
    }
    requestAnimationFrame(animate);
  }

  function getCell(cellId) {
    return cells[cellId - 1];
  }

  return {
    init: init,
    updateCells: updateCells,
    resizeCanvas: resizeCanvas,
    startAnimation: startAnimation,
    getCell: getCell
  };
})();
