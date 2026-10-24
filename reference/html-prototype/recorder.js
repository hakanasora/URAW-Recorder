(() => {
  const root = document.getElementById('umik-monitor-modules');
  if (!root || root.dataset.initialized) return;
  root.dataset.initialized = 'true';
  const q = selector => root.querySelector(selector);
  const qa = selector => [...root.querySelectorAll(selector)];
  const saved = globalThis.openai?.widgetState?.modelContent;
  const state = {
    display: ['level', 'spectrum', 'pitch'].includes(saved?.display) ? saved.display : 'level'
  };
  const input = { device: 'umik1', calibration: '7146299-0', gainDb: 0 };
  const file = { format: 'wav', mp3: '320', flac: '5', path: '', directoryNameOnly: false };
  const compressionOptions = {
    mp3: [['320', '320 kbps · 最高品质'], ['256', '256 kbps · 高品质'], ['192', '192 kbps · 标准'], ['128', '128 kbps · 较小体积']],
    flac: [['0', '快速 · 等级 0'], ['5', '标准 · 等级 5'], ['8', '最小体积 · 等级 8']]
  };
  function restoreFile(data, privateData) {
    if (['wav', 'flac', 'mp3'].includes(data?.format)) file.format = data.format;
    for (const format of ['mp3', 'flac']) {
      if (compressionOptions[format].some(([value]) => value === data?.[format])) file[format] = data[format];
    }
    if (typeof privateData?.path === 'string') {
      file.path = privateData.path.slice(0, 1024);
      file.directoryNameOnly = privateData.directoryNameOnly === true;
    }
  }
  restoreFile(saved?.file, globalThis.openai?.widgetState?.privateContent?.file);
  let calibrations = [
    { id: '7146299-0', name: '7146299.txt' },
    { id: '7146299-90', name: '7146299_90deg.txt' }
  ];
  // File objects remain local to this preview; only list metadata is remembered.
  const calibrationFiles = new Map();
  const ui = { inputOpen: globalThis.openai?.widgetState?.privateContent?.inputOpen === true };
  function restoreCalibrations(entries) {
    if (!Array.isArray(entries)) return;
    const seen = new Set();
    calibrations = entries.slice(0, 16).filter(entry => {
      if (typeof entry?.id !== 'string' || !entry.id || entry.id === 'none' || entry.id.length > 64
        || typeof entry.name !== 'string' || entry.name.length > 255 || !/\.txt$/i.test(entry.name) || seen.has(entry.id)) return false;
      seen.add(entry.id);
      return true;
    }).map(({ id, name }) => ({ id, name }));
  }
  function restoreInput(data) {
    if (['umik1', 'default'].includes(data?.device)) input.device = data.device;
    // Migrate the previous paired-profile choice without retaining an angle UI.
    const selected = data?.calibration === '7146299' ? `7146299-${data.orientation === '90' ? '90' : '0'}` : data?.calibration;
    if (selected === 'none' || calibrations.some(entry => entry.id === selected)) input.calibration = selected;
    if (Number.isFinite(data?.gainDb)) input.gainDb = Math.round(Math.max(-60, Math.min(24, data.gainDb)) * 10) / 10;
    if (!calibrations.some(entry => entry.id === input.calibration)) input.calibration = 'none';
  }
  restoreCalibrations(globalThis.openai?.widgetState?.privateContent?.calibrations);
  restoreInput(saved?.input);
  const recording = { phase: 'idle', elapsedMs: 0, startedAt: 0 };
  let clockFrame = null;
  let lastClockPaint = 0;
  let transportFlight = null;
  const monitor = { channels: [{ rms: -32, peak: -18.6, vu: -11 }], heldPeak: -18.6 + input.gainDb };
  const pitch = {
    midi: 47, elapsed: 0, low: 36, targetLow: 36, span: 36,
    lastFrame: null, lastSample: 0,
    history: Array.from({ length: 81 }, (_, index) => ({ time: (index - 80) / 20, midi: 47 + .08 * Math.sin(index * .7) }))
  };
  const noteNames = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];
  const vuFaces = new WeakMap();
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  let monitorFrame = null;
  let lastMonitorUpdate = 0;
  const design = { appearance: '深色', gap: 0, radius: 0, accent: '#FFB700' };
  const probe = document.createElement('span');
  probe.hidden = true;
  root.append(probe);
  const colors = {};
  function resolveColors() {
    for (const key of ['text', 'muted', 'line', 'grid', 'accent', 'peak']) {
      probe.style.color = `var(--mm-${key})`;
      colors[key] = getComputedStyle(probe).color;
    }
  }
  function canvasContext(selector) {
    const element = typeof selector === 'string' ? q(selector) : selector;
    const { width, height } = element.getBoundingClientRect();
    if (width < 1 || height < 1) return null;
    const dpr = Math.min(devicePixelRatio || 1, 2);
    element.width = Math.round(width * dpr);
    element.height = Math.round(height * dpr);
    const ctx = element.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx, width, height };
  }
  function label(ctx, content, x, y, align = 'left', color = colors.muted, size = 11) {
    ctx.fillStyle = color;
    ctx.font = `${size}px "Segoe UI", sans-serif`;
    ctx.textAlign = align;
    ctx.fillText(content, x, y);
  }
  function drawSpectrum() {
    const box = canvasContext('.mm-spectrum-plot');
    if (!box) return;
    const { ctx, width: w, height: h } = box;
    const left = 46, right = w - 22, top = 8, bottom = h - 42;
    const plotHeight = bottom - top;
    const xFor = frequency => left + Math.log10(frequency / 20) / 3 * (right - left);
    const yFor = db => top + (6 - db) / 96 * plotHeight;
    const levels = [-12, -30, -48, -66, -84];
    const majorFrequencies = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];
    const frequencies = w < 480 ? [20, 200, 2000, 20000] : majorFrequencies;
    // This visual gain follows the same unweighted RMS snapshot as the meters.
    // The narrow demo range makes the effect visible without changing the data;
    // there is no peak hold, release tail, or feedback into recorded audio.
    const rms = Math.max(...monitor.channels.map(channel => channel.rms)) + input.gainDb;
    const brightness = Math.max(0, Math.min(1, (rms + 34) / 4));
    const bloom = brightness * brightness;
    const rasterScale = ctx.canvas.width / w;

    // Minor grid positions follow the logarithmic frequency scale, rather than
    // drawing an evenly spaced decorative mesh behind logarithmic labels.
    ctx.save();
    ctx.beginPath(); ctx.rect(left, top, right - left, plotHeight); ctx.clip();
    ctx.lineWidth = .65; ctx.strokeStyle = colors.grid; ctx.globalAlpha = .48;
    for (let db = -88.5; db <= 6; db += 4.5) {
      if (levels.includes(db)) continue;
      const y = yFor(db);
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(right, y); ctx.stroke();
    }
    const gridStep = w < 480 ? 2 : 1;
    for (let decade = 10; decade <= 10000; decade *= 10) {
      for (let part = 2; part < 20; part += gridStep) {
        const frequency = decade * part / 2;
        if (frequency < 20 || frequency > 20000 || majorFrequencies.includes(frequency)) continue;
        const x = xFor(frequency);
        ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x, bottom); ctx.stroke();
      }
    }
    ctx.lineWidth = .8; ctx.strokeStyle = colors.line; ctx.globalAlpha = .78;
    levels.forEach(db => {
      const y = yFor(db);
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(right, y); ctx.stroke();
    });
    majorFrequencies.forEach(frequency => {
      const x = xFor(frequency);
      ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x, bottom); ctx.stroke();
    });
    ctx.restore();
    const points = [];
    for (let i = 0; i <= 210; i++) {
      const u = i / 210;
      let amplitude = .14 + .11 * Math.exp(-Math.pow((u - .14) / .12, 2))
        + .48 * Math.exp(-Math.pow((u - .49) / .24, 2))
        + .16 * Math.exp(-Math.pow((u - .79) / .12, 2));
      amplitude += .018 * Math.sin(u * 33) + .009 * Math.cos(u * 52);
      points.push([left + u * (right - left), Math.max(top, Math.min(bottom, yFor(-84 + amplitude * 72 + input.gainDb)))]);
    }
    ctx.save();
    ctx.beginPath(); ctx.moveTo(left, bottom);
    points.forEach(point => ctx.lineTo(...point));
    ctx.lineTo(right, bottom); ctx.closePath();
    ctx.clip();
    const hex = design.accent.replace('#', '');
    const expanded = hex.length === 3 ? hex.split('').map(c => c + c).join('') : hex;
    const rgb = [0, 2, 4].map(i => parseInt(expanded.slice(i, i + 2), 16));
    const tint = (desaturation, alpha) => {
      const neutral = [152, 145, 124];
      const channels = rgb.map((value, i) => Math.round(value * (1 - desaturation) + neutral[i] * desaturation));
      return `rgba(${channels.join(',')},${alpha})`;
    };
    // Each column starts its gradient at the curve itself, so the saturated edge
    // follows every rise and fall instead of fading from a global chart height.
    for (let x = Math.floor(left); x < right; x++) {
      const position = Math.max(0, (x - left) / (right - left) * (points.length - 1));
      const i = Math.min(points.length - 2, Math.floor(position));
      const fraction = position - i;
      const y = points[i][1] * (1 - fraction) + points[i + 1][1] * fraction;
      const depth = Math.max(1, Math.min(plotHeight * .46, bottom - y));
      const shade = ctx.createLinearGradient(0, y, 0, y + depth);
      shade.addColorStop(0, tint(0, .84));
      shade.addColorStop(.14, tint(.06, .58));
      shade.addColorStop(.42, tint(.27, .30));
      shade.addColorStop(.76, tint(.64, .075));
      shade.addColorStop(1, tint(1, 0));
      ctx.globalAlpha = (.84 + .16 * Math.cos((x - left) * Math.PI)) * (.82 + .18 * brightness);
      ctx.fillStyle = shade;
      ctx.fillRect(x, y, 1.1, bottom - y);
    }
    ctx.restore();
    ctx.beginPath(); points.forEach((point, i) => i ? ctx.lineTo(...point) : ctx.moveTo(...point));
    ctx.save();
    ctx.lineJoin = 'round'; ctx.lineCap = 'round';
    ctx.shadowColor = tint(0, .9);
    ctx.shadowBlur = (3 + 23 * bloom) * rasterScale;
    ctx.strokeStyle = tint(0, .015 + .24 * bloom);
    ctx.lineWidth = 2 + 5 * bloom;
    ctx.stroke();
    ctx.shadowBlur = (2 + 8 * bloom) * rasterScale;
    ctx.strokeStyle = tint(0, .035 + .48 * bloom);
    ctx.lineWidth = 2.5;
    ctx.stroke();
    ctx.restore();
    ctx.lineWidth = 2.2; ctx.lineJoin = 'round'; ctx.lineCap = 'round';
    ctx.strokeStyle = colors.accent; ctx.stroke();
    ctx.save();
    ctx.globalAlpha = .38 * bloom;
    ctx.strokeStyle = `rgb(${rgb.map(value => Math.round(value * .65 + 255 * .35)).join(',')})`;
    ctx.lineWidth = .9; ctx.stroke();
    ctx.restore();

    // Feather the grid and spectrum together; axis text is drawn afterwards.
    // There is no hard enclosing frame or opaque vignette over the labels.
    ctx.save();
    ctx.globalCompositeOperation = 'destination-in';
    const horizontalFade = ctx.createLinearGradient(left, 0, right, 0);
    [[0, 0], [.025, .55], [.06, 1], [.94, 1], [.975, .55], [1, 0]].forEach(([stop, alpha]) => {
      horizontalFade.addColorStop(stop, `rgba(0,0,0,${alpha})`);
    });
    ctx.fillStyle = horizontalFade; ctx.fillRect(0, 0, w, h);
    const verticalFade = ctx.createLinearGradient(0, top, 0, bottom);
    [[0, 0], [.07, .5], [.16, 1], [.86, 1], [.98, .18], [1, 0]].forEach(([stop, alpha]) => {
      verticalFade.addColorStop(stop, `rgba(0,0,0,${alpha})`);
    });
    ctx.fillStyle = verticalFade; ctx.fillRect(0, 0, w, h);
    ctx.restore();

    levels.forEach(db => label(ctx, `${db}`, left - 11, yFor(db) + 4, 'right'));
    frequencies.forEach((frequency, i) => {
      label(ctx, frequency >= 1000 ? `${frequency / 1000}k` : `${frequency}`, xFor(frequency), bottom + 22,
        i === 0 ? 'left' : i === frequencies.length - 1 ? 'right' : 'center');
    });
    label(ctx, 'dB', left - 11, top + 5, 'right');
    label(ctx, 'Hz', right, h - 5, 'right');
  }
  function drawVu(element, vuValue) {
    const box = canvasContext(element);
    if (!box) return;
    const { ctx, width: w, height: h } = box;
    const cx = w / 2, cy = h - 9;
    const radius = Math.max(30, Math.min(w * .45, (cx - 18) / Math.cos(27 * Math.PI / 180) - 14, 104));
    const marks = [-20, -10, -7, -5, -3, 0, 3];
    const angles = [-153, -132, -111, -90, -69, -48, -27];
    function angle(value) {
      let i = 0;
      while (i < marks.length - 2 && value > marks[i + 1]) i++;
      return (angles[i] + (value - marks[i]) / (marks[i + 1] - marks[i]) * (angles[i + 1] - angles[i])) * Math.PI / 180;
    }
    const hex = design.accent.slice(1);
    const expanded = hex.length === 3 ? hex.split('').map(c => c + c).join('') : hex;
    const accent = [0, 2, 4].map(i => parseInt(expanded.slice(i, i + 2), 16));
    const amber = [accent[0], Math.round(accent[1] * .82), accent[2]];
    const ivory = accent.map((value, i) => Math.round(value * .18 + [255, 240, 214][i] * .82));
    const rgba = (rgb, alpha = 1) => `rgba(${rgb.join(',')},${alpha})`;
    const scaleInk = design.appearance === '浅色' ? colors.text : rgba(ivory);
    const positive = '#F38B58';
    const rasterScale = ctx.canvas.width / w;

    const faceKey = [w, h, ctx.canvas.width, colors.accent, colors.text, design.appearance].join('|');
    const cachedFace = vuFaces.get(element);
    if (cachedFace?.key === faceKey) {
      ctx.drawImage(cachedFace.canvas, 0, 0, w, h);
    } else {
      // A continuous fan of light rises from each lamp. The bottom fade is applied
      // before drawing the dial, so it never softens the lettering or needle.
      function glow(x, y, rx, ry, stops) {
        ctx.save();
        ctx.translate(x, y); ctx.scale(rx, ry);
        const light = ctx.createRadialGradient(0, 0, 0, 0, 0, 1);
        stops.forEach(([position, color]) => light.addColorStop(position, color));
        ctx.fillStyle = light;
        ctx.fillRect(-1, -1, 2, 2);
        ctx.restore();
      }
      glow(cx, cy - radius * .28, radius * 1.02, radius * .71, [
        [0, rgba(amber, .14)], [.45, rgba(amber, .095)], [1, rgba(amber, 0)]
      ]);
      for (const side of [-1, 1]) {
        const x = cx + side * radius * .38;
        ctx.save();
        ctx.filter = `blur(${radius * .065 * rasterScale}px)`;
        const beam = ctx.createLinearGradient(0, cy + 2, 0, cy - radius * .84);
        beam.addColorStop(0, rgba(accent, .96));
        beam.addColorStop(.12, rgba(amber, .86));
        beam.addColorStop(.38, rgba(amber, .53));
        beam.addColorStop(.72, rgba(amber, .19));
        beam.addColorStop(1, rgba(amber, 0));
        ctx.fillStyle = beam;
        ctx.beginPath();
        ctx.moveTo(x - radius * .12, cy + 3);
        ctx.bezierCurveTo(x - radius * .24, cy - radius * .12,
          x - radius * .70, cy - radius * .42, x - radius * .54, cy - radius * .72);
        ctx.bezierCurveTo(x - radius * .30, cy - radius * .91,
          x + radius * .30, cy - radius * .91, x + radius * .54, cy - radius * .72);
        ctx.bezierCurveTo(x + radius * .70, cy - radius * .42,
          x + radius * .24, cy - radius * .12, x + radius * .12, cy + 3);
        ctx.closePath(); ctx.fill();
        ctx.restore();
        glow(x, cy - 3, radius * .19, radius * .13, [
          [0, rgba(ivory, 1)], [.14, rgba(accent, .98)],
          [.42, rgba(amber, .76)], [.72, rgba(amber, .23)], [1, rgba(amber, 0)]
        ]);
      }
      ctx.save();
      ctx.globalCompositeOperation = 'destination-in';
      const bottomFade = ctx.createLinearGradient(0, cy - 8, 0, cy + 7);
      bottomFade.addColorStop(0, 'rgba(0,0,0,1)');
      bottomFade.addColorStop(.44, 'rgba(0,0,0,1)');
      bottomFade.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = bottomFade; ctx.fillRect(0, 0, w, h);
      ctx.restore();

      // A fine reflection suggests the dark curved dial without adding a case.
      const reflection = ctx.createLinearGradient(0, cy - radius - 17, 0, cy);
      reflection.addColorStop(0, rgba(ivory, .36));
      reflection.addColorStop(.55, rgba(amber, .12));
      reflection.addColorStop(1, rgba(amber, 0));
      ctx.lineWidth = .8; ctx.strokeStyle = reflection;
      ctx.beginPath();
      ctx.arc(cx, cy, radius + 16, -153 * Math.PI / 180, -27 * Math.PI / 180);
      ctx.stroke();

      ctx.save();
      ctx.shadowColor = rgba(amber, .8); ctx.shadowBlur = 3 * rasterScale;
      ctx.lineWidth = 1; ctx.strokeStyle = rgba(ivory, .65);
      ctx.beginPath();
      ctx.arc(cx, cy, radius, -153 * Math.PI / 180, angle(0));
      ctx.stroke();
      ctx.strokeStyle = positive;
      ctx.beginPath(); ctx.arc(cx, cy, radius, angle(0), angle(3)); ctx.stroke();
      for (let i = 0; i <= 36; i++) {
        const a = (-153 + i * 126 / 36) * Math.PI / 180;
        ctx.strokeStyle = a > angle(0) ? positive : rgba(ivory, .86);
        ctx.beginPath();
        ctx.moveTo(cx + (radius - 4) * Math.cos(a), cy + (radius - 4) * Math.sin(a));
        ctx.lineTo(cx + radius * Math.cos(a), cy + radius * Math.sin(a));
        ctx.stroke();
      }
      ctx.restore();
      function litLabel(content, x, y, color, size = 11) {
        ctx.save();
        ctx.shadowColor = color === positive ? positive : rgba(amber, .85);
        ctx.shadowBlur = 4.5 * rasterScale;
        label(ctx, content, x, y, 'center', color, size);
        ctx.shadowBlur = 1.2 * rasterScale;
        label(ctx, content, x, y, 'center', color, size);
        ctx.restore();
      }
      marks.forEach((mark, i) => {
        const a = angles[i] * Math.PI / 180;
        ctx.save();
        ctx.strokeStyle = mark > 0 ? positive : scaleInk;
        ctx.shadowColor = mark > 0 ? positive : rgba(amber, .8);
        ctx.shadowBlur = 3 * rasterScale;
        ctx.beginPath();
        ctx.moveTo(cx + (radius - 8) * Math.cos(a), cy + (radius - 8) * Math.sin(a));
        ctx.lineTo(cx + radius * Math.cos(a), cy + radius * Math.sin(a));
        ctx.stroke();
        ctx.restore();
        if (w > 180 || [-20, -5, 0, 3].includes(mark)) {
          litLabel(mark > 0 ? `+${mark}` : `${mark}`, cx + (radius + 14) * Math.cos(a),
            cy + (radius + 14) * Math.sin(a) + 4, mark > 0 ? positive : scaleInk);
        }
      });
      litLabel('VU', cx + 24, cy - 24, scaleInk, 16);
      const faceCanvas = document.createElement('canvas');
      faceCanvas.width = ctx.canvas.width;
      faceCanvas.height = ctx.canvas.height;
      faceCanvas.getContext('2d').drawImage(ctx.canvas, 0, 0);
      vuFaces.set(element, { key: faceKey, canvas: faceCanvas });
    }
    const a = angle(Math.max(-20, Math.min(3, vuValue)));
    ctx.save();
    ctx.translate(cx, cy); ctx.rotate(a);
    // Draw the warm halo first, then keep a narrow, bright needle at its centre.
    ctx.save();
    ctx.shadowColor = rgba(amber, .95); ctx.shadowBlur = 6 * rasterScale;
    ctx.strokeStyle = rgba(accent, .65); ctx.lineWidth = 2.5;
    ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(radius - 5, 0); ctx.stroke();
    ctx.restore();
    const needle = ctx.createLinearGradient(0, -1.5, 0, 1.5);
    needle.addColorStop(0, rgba(ivory));
    needle.addColorStop(.4, rgba(accent));
    needle.addColorStop(1, rgba(amber));
    ctx.shadowColor = 'rgba(0,0,0,.75)'; ctx.shadowBlur = 2;
    ctx.shadowOffsetX = .7; ctx.shadowOffsetY = 1.2;
    ctx.fillStyle = needle;
    ctx.beginPath(); ctx.moveTo(-3, -1.4);
    ctx.lineTo(radius - 5, -.25); ctx.lineTo(radius - 5, .25);
    ctx.lineTo(-3, 1.4); ctx.closePath(); ctx.fill();
    ctx.shadowBlur = 0; ctx.shadowOffsetX = 0; ctx.shadowOffsetY = 0;
    ctx.strokeStyle = rgba(ivory, .8); ctx.lineWidth = .65;
    ctx.beginPath(); ctx.moveTo(0, -.55); ctx.lineTo(radius - 7, -.15); ctx.stroke();
    ctx.restore();

    ctx.save();
    ctx.shadowColor = 'rgba(0,0,0,.6)'; ctx.shadowBlur = 3; ctx.shadowOffsetY = 1;
    ctx.beginPath(); ctx.arc(cx, cy, 6.7, 0, Math.PI * 2);
    ctx.fillStyle = '#0C0D0D'; ctx.fill();
    ctx.shadowBlur = 0; ctx.shadowOffsetY = 0;
    const hub = ctx.createRadialGradient(cx - 1.8, cy - 2.2, .4, cx, cy, 5.3);
    hub.addColorStop(0, rgba(ivory));
    hub.addColorStop(.32, rgba(accent));
    hub.addColorStop(.8, rgba(amber));
    hub.addColorStop(1, rgba(amber.map(value => Math.round(value * .67))));
    ctx.beginPath(); ctx.arc(cx, cy, 5.3, 0, Math.PI * 2);
    ctx.fillStyle = hub; ctx.fill();
    ctx.restore();
  }
  function demoPitch(time) {
    // A repeatable low -> high -> low phrase makes both directions visible.
    // This fixture is not microphone data or a fundamental-frequency estimator.
    const keys = [[0, 47], [2, 47], [3.2, 53], [4.4, 60], [5.8, 76], [7.2, 79], [8.2, 79], [9.5, 60], [10.8, 38], [12.5, 38], [14, 45], [15.5, 47], [18, 47]];
    const phase = time % 18;
    let index = 0;
    while (index < keys.length - 2 && phase > keys[index + 1][0]) index++;
    const [fromTime, fromMidi] = keys[index], [toTime, toMidi] = keys[index + 1];
    const u = Math.max(0, Math.min(1, (phase - fromTime) / (toTime - fromTime)));
    const eased = u * u * (3 - 2 * u);
    return fromMidi + (toMidi - fromMidi) * eased + .08 * Math.sin(time * 17) + .04 * Math.sin(time * 7.4);
  }
  function updatePitchReadouts() {
    const nearest = Math.round(pitch.midi);
    const frequency = 440 * Math.pow(2, (pitch.midi - 69) / 12);
    const cents = Math.round((pitch.midi - nearest) * 100);
    q('[data-pitch-name]').textContent = noteNames[nearest % 12];
    q('[data-pitch-octave]').textContent = Math.floor(nearest / 12) - 1;
    q('[data-pitch-frequency]').textContent = frequency.toFixed(2);
    q('.mm-cents-canvas').setAttribute('aria-label', `音分刻度尺，范围负 50 至正 50 音分；当前 ${cents} 音分，模拟数据`);
  }
  function advancePitch(dt) {
    let changed = false;
    pitch.elapsed += dt;
    if (pitch.elapsed - pitch.lastSample >= .05) {
      pitch.midi = demoPitch(pitch.elapsed);
      pitch.lastSample = pitch.elapsed;
      pitch.history.push({ time: pitch.elapsed, midi: pitch.midi });
      pitch.history = pitch.history.filter(point => point.time >= pitch.elapsed - 4.2);
      updatePitchReadouts();
      changed = true;
    }
    // Keep a fixed 36-semitone scale. Only the newest pitch drives the viewport;
    // old out-of-range history is clipped instead of stretching the graph.
    // A 3-semitone trigger and 7-semitone landing margin prevent edge jitter.
    if (pitch.midi > pitch.targetLow + pitch.span - 3) pitch.targetLow = Math.ceil(pitch.midi + 7 - pitch.span);
    else if (pitch.midi < pitch.targetLow + 3) pitch.targetLow = Math.floor(pitch.midi - 7);
    pitch.targetLow = Math.max(0, Math.min(127 - pitch.span, pitch.targetLow));
    const previousLow = pitch.low;
    if (reducedMotion.matches) pitch.low = pitch.targetLow;
    else {
      // First-order, frame-rate-independent easing: ~95% settled in 165 ms.
      pitch.low += (pitch.targetLow - pitch.low) * (1 - Math.exp(-dt / .055));
      if (Math.abs(pitch.targetLow - pitch.low) < .005) pitch.low = pitch.targetLow;
    }
    return changed || Math.abs(pitch.low - previousLow) > .0001;
  }
  function drawCents() {
    const box = canvasContext('.mm-cents-canvas');
    if (!box) return;
    const { ctx, width: w, height: h } = box;
    const left = 7, right = w - 7, center = w / 2;
    const cents = (pitch.midi - Math.round(pitch.midi)) * 100;
    const xFor = value => left + (value + 50 - cents) / 100 * (right - left);
    const top = 6;
    for (let value = -50; value <= 50; value += 5) {
      const major = value % 25 === 0;
      const x = xFor(value);
      ctx.strokeStyle = major ? colors.text : colors.muted;
      ctx.globalAlpha = major ? .8 : .55;
      ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x, top + (major ? 0 : 6)); ctx.lineTo(x, top + 18); ctx.stroke();
      if (major) {
        ctx.globalAlpha = .85;
        label(ctx, value === 0 ? '0' : value > 0 ? `+${value}` : `−${Math.abs(value)}`, x, h - 5, 'center');
      }
    }
    ctx.globalAlpha = 1;
    ctx.save();
    ctx.globalCompositeOperation = 'destination-in';
    const fade = ctx.createLinearGradient(0, 0, w, 0);
    [[0, 0], [.12, .5], [.27, 1], [.73, 1], [.88, .5], [1, 0]].forEach(([stop, alpha]) => {
      fade.addColorStop(stop, `rgba(0,0,0,${alpha})`);
    });
    ctx.fillStyle = fade; ctx.fillRect(0, 0, w, h);
    ctx.restore();
    // The fixed centre mark is the target; the straight ruler shifts by cents.
    ctx.strokeStyle = colors.accent; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(center, top - 3); ctx.lineTo(center, top + 21); ctx.stroke();
  }
  function drawPitch() {
    const box = canvasContext('.mm-pitch-canvas');
    if (!box) return;
    const { ctx, width: w, height: h } = box;
    const left = 32, right = w - 6, top = 17, bottom = h - 28;
    const low = pitch.low, high = low + pitch.span;
    const yFor = midi => top + (high - midi) / (high - low) * (bottom - top);
    const rows = [];
    for (let midi = Math.ceil(low); midi <= Math.floor(high); midi++) {
      if (midi % 12 === 0 || (h >= 230 && midi % 12 === 7)) {
        rows.push([midi, `${noteNames[midi % 12]}${Math.floor(midi / 12) - 1}`]);
      }
    }
    ctx.save();
    ctx.beginPath(); ctx.rect(left, top, right - left, bottom - top); ctx.clip();
    ctx.lineWidth = .65; ctx.strokeStyle = colors.grid; ctx.globalAlpha = .45;
    for (let midi = Math.ceil(low / 3) * 3; midi <= high; midi += 3) {
      const y = yFor(midi);
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(right, y); ctx.stroke();
    }
    ctx.globalAlpha = 1;
    rows.forEach(([midi, name]) => {
      const y = yFor(midi);
      ctx.strokeStyle = colors.line;
      ctx.globalAlpha = midi % 12 === 0 ? .85 : .6;
      ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(right, y); ctx.stroke();
    });
    ctx.globalAlpha = 1;
    ctx.beginPath();
    let started = false;
    for (const point of pitch.history) {
      if (!Number.isFinite(point.midi)) { started = false; continue; }
      const u = (point.time - (pitch.elapsed - 4)) / 4;
      if (u < 0 || u > 1) continue;
      const x = left + u * (right - left) * .94, y = yFor(point.midi);
      if (!started) { ctx.moveTo(x, y); started = true; }
      else ctx.lineTo(x, y);
    }
    ctx.strokeStyle = colors.accent; ctx.lineWidth = 1.45; ctx.lineJoin = 'round'; ctx.stroke();
    ctx.restore();
    ctx.strokeStyle = colors.muted; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(left, top); ctx.lineTo(left, bottom); ctx.stroke();
    ctx.save();
    ctx.globalCompositeOperation = 'destination-in';
    const fade = ctx.createLinearGradient(left, 0, right, 0);
    [[0, 1], [.76, 1], [.9, .65], [1, 0]].forEach(([stop, alpha]) => {
      fade.addColorStop(stop, `rgba(0,0,0,${alpha})`);
    });
    ctx.fillStyle = fade; ctx.fillRect(0, 0, w, h);
    ctx.restore();
    rows.forEach(([midi, name]) => label(ctx, name, left - 8, yFor(midi) + 4, 'right'));
    label(ctx, '−4 s', left, h - 7);
    label(ctx, '现在', right - 4, h - 7, 'right');
  }
  function draw() {
    resolveColors();
    if (state.display === 'spectrum') drawSpectrum();
    else if (state.display === 'pitch') { drawPitch(); drawCents(); }
    else qa('.mm-vu-canvas').forEach((element, index) => {
      const channel = monitor.channels[index] || monitor.channels[0];
      drawVu(element, channel.vu + input.gainDb);
    });
  }
  function announce(message) { q('.mm-status').textContent = message; }
  function save() {
    if (globalThis.openai?.setWidgetState) {
      globalThis.openai.setWidgetState({
        modelContent: { display: state.display, input: { ...input }, file: { format: file.format, mp3: file.mp3, flac: file.flac } },
        privateContent: { inputOpen: ui.inputOpen, calibrations: calibrations.map(({ id, name }) => ({ id, name })), file: { path: file.path, directoryNameOnly: file.directoryNameOnly } }
      }).catch(() => {});
    }
  }
  function renderInput() {
    root.dataset.inputOpen = String(ui.inputOpen);
    const panel = q('.mm-input-drawer');
    panel.inert = !ui.inputOpen;
    panel.setAttribute('aria-hidden', String(!ui.inputOpen));
    const toggle = q('[data-input-toggle]');
    toggle.setAttribute('aria-expanded', String(ui.inputOpen));
    toggle.setAttribute('aria-label', ui.inputOpen ? '收起设置面板' : '打开设置面板');
    const select = q('[data-input-field="calibration"]');
    const options = calibrations.map(entry => new Option(entry.name, entry.id));
    options.push(new Option('不使用校准', 'none'));
    select.replaceChildren(...options);
    qa('[data-input-field]').forEach(field => { field.value = input[field.dataset.inputField]; });
    renderGain();
    renderFile();
  }
  function renderDirectoryFeedback() {
    const feedback = q('[data-directory-feedback]');
    feedback.hidden = !file.directoryNameOnly;
    feedback.textContent = file.directoryNameOnly ? '已选择文件夹；浏览器仅提供目录名，完整路径可手动补全。' : '';
  }
  function renderFile() {
    q('[data-save-path]').value = file.path;
    q('[data-record-format]').value = file.format;
    const compressed = file.format !== 'wav';
    q('[data-compression-field]').hidden = !compressed;
    const quality = q('[data-compression-quality]');
    quality.disabled = !compressed;
    if (compressed) {
      quality.replaceChildren(...compressionOptions[file.format].map(([value, label]) => new Option(label, value)));
      quality.value = file[file.format];
      q('[data-compression-hint]').textContent = file.format === 'flac'
        ? '始终无损；等级影响体积与编码耗时。' : '码率越高，保留的细节越多，文件也越大。';
    }
    renderDirectoryFeedback();
  }
  function chooseDirectory(name) {
    // Browsers disclose a directory name, never an absolute OS path. Do not
    // fabricate a prefix or retain a handle that implies actual output access.
    if (!name) return;
    file.path = name.slice(0, 1024);
    file.directoryNameOnly = true;
    renderFile(); save();
    announce(`已选择文件夹 ${name}；完整路径可手动补全`);
  }
  function renderCalibrationManager() {
    const list = q('[data-calibration-list]');
    const rows = calibrations.map(entry => {
      const row = document.createElement('li');
      const info = document.createElement('div');
      const name = document.createElement('span');
      name.className = 'mm-calibration-entry-name'; name.textContent = entry.name;
      const status = document.createElement('span');
      status.className = 'mm-calibration-entry-state';
      status.textContent = input.calibration === entry.id ? '当前选择' : '已添加';
      info.append(name, status);
      const remove = document.createElement('button');
      remove.type = 'button'; remove.className = 'mm-text-link mm-calibration-remove cursor-interaction';
      remove.dataset.removeCalibration = entry.id; remove.textContent = '移除';
      remove.setAttribute('aria-label', `从列表移除 ${entry.name}`);
      row.append(info, remove);
      return row;
    });
    list.replaceChildren(...rows);
    q('[data-calibration-empty]').hidden = calibrations.length > 0;
  }
  function formatGain(value) {
    if (Math.abs(value) < .05) return '0 dB';
    return `${value < 0 ? '−' : '+'}${Math.abs(value).toFixed(1)} dB`;
  }
  function renderGain() {
    const gain = q('[data-gain-input]');
    gain.value = input.gainDb;
    gain.setAttribute('aria-valuetext', input.gainDb === 0 ? '0 dB，不增不减' : formatGain(input.gainDb));
    q('[data-gain-value]').textContent = formatGain(input.gainDb);
    q('.mm-gain-control').dataset.gainZero = String(input.gainDb === 0);
    const angle = input.gainDb < 0 ? input.gainDb / 60 * 135 : input.gainDb / 24 * 135;
    q('.mm-gain-control').style.setProperty('--mm-gain-angle', `${angle}deg`);
  }
  function setGain(value) {
    if (!Number.isFinite(value)) return;
    input.gainDb = Math.round(Math.max(-60, Math.min(24, value)) * 10) / 10;
    if (Object.is(input.gainDb, -0)) input.gainDb = 0;
    renderGain(); updateMeterReadouts(true); draw();
  }
  function setInputOpen(open) {
    // Return focus before making the closing panel inert.
    if (!open && q('.mm-input-drawer').contains(document.activeElement)) {
      q('[data-input-toggle]').focus({ preventScroll: true });
    }
    ui.inputOpen = open;
    renderInput();
    if (open) q('[data-input-field="device"]').focus({ preventScroll: true });
    save();
    announce(open ? '输入与文件设置已展开' : '设置面板已收起');
  }
  function render() {
    root.dataset.display = state.display;
    q('[data-display-select]').value = state.display;
    qa('[data-display-panel]').forEach(panel => { panel.hidden = panel.dataset.displayPanel !== state.display; });
    renderInput();
    updateMeterReadouts();
    requestAnimationFrame(draw);
    syncMonitoring();
  }
  function updateMeterReadouts(capturePeak = false) {
    const sourcePeak = Math.max(...monitor.channels.map(channel => channel.peak));
    const rawLevel = Math.max(...monitor.channels.map(channel => channel.rms)) + input.gainDb;
    const peak = sourcePeak + input.gainDb;
    if (capturePeak) monitor.heldPeak = monitor.heldPeak === null ? peak : Math.max(monitor.heldPeak, peak);
    const formattedLevel = rawLevel.toFixed(1);
    q('[data-level-minus]').hidden = !formattedLevel.startsWith('-');
    q('[data-level-digits]').textContent = formattedLevel.replace('-', '');
    q('[data-value="level"]').setAttribute('aria-label', `${formattedLevel.replace('-', '−')} dBFS RMS`);
    const held = monitor.heldPeak;
    q('[data-value="peak"]').textContent = held === null ? '—' : `${held > 0 ? '+' : ''}${held.toFixed(1).replace('-', '−')}`;
    q('.mm-digital-peak').dataset.overload = String(held !== null && held >= 0);
    // The lamp follows current unweighted input/output peaks, not the held peak
    // or recording phase. Attenuation cannot repair an already-clipped source.
    root.dataset.clipping = String(sourcePeak >= 0 || peak >= 0);
    updatePeakIndicator();
    q('[data-value="level-unit"]').textContent = 'dBFS · RMS';
  }
  // This is a mono demonstration frame. The renderer also accepts two distinct
  // channel states; a mono frame is reused for both dials without resampling.
  function demoMonitorFrame(time) {
    return [{
      rms: -32 + 1.6 * Math.sin(time * .9) + .35 * Math.sin(time * 2.2),
      peak: -18.6 + 1.3 * Math.sin(time * .9 + .3),
      vu: -11 + 2 * Math.sin(time * .9) + .4 * Math.sin(time * 2.2)
    }];
  }
  function applyMonitorFrame(channels) {
    monitor.channels = channels;
    updateMeterReadouts(true);
    if (state.display === 'level' || state.display === 'spectrum') draw();
  }
  function monitorTick(now) {
    monitorFrame = null;
    if (!root.isConnected || document.hidden || reducedMotion.matches) return;
    if (now - lastMonitorUpdate >= 50) {
      applyMonitorFrame(demoMonitorFrame(now / 1000));
      lastMonitorUpdate = now;
    }
    if (state.display === 'pitch') {
      const dt = pitch.lastFrame === null ? 0 : Math.min(.1, Math.max(0, (now - pitch.lastFrame) / 1000));
      pitch.lastFrame = now;
      if (advancePitch(dt)) draw();
    } else pitch.lastFrame = null;
    monitorFrame = requestAnimationFrame(monitorTick);
  }
  function syncMonitoring() {
    const animate = root.isConnected && !document.hidden && !reducedMotion.matches;
    if (animate && monitorFrame === null) monitorFrame = requestAnimationFrame(monitorTick);
    if (!animate && monitorFrame !== null) {
      cancelAnimationFrame(monitorFrame);
      monitorFrame = null;
    }
    if (!animate || state.display !== 'pitch') pitch.lastFrame = null;
    if (reducedMotion.matches) {
      pitch.low = pitch.targetLow;
      applyMonitorFrame([{ rms: -32, peak: -18.6, vu: -11 }]);
      if (state.display === 'pitch') draw();
    }
  }
  function updateClock() {
    const clock = q('[data-record-clock]');
    const active = ['recording', 'paused'].includes(recording.phase);
    clock.hidden = !active;
    if (!active) return;
    const elapsed = recording.elapsedMs + (recording.phase === 'recording' ? performance.now() - recording.startedAt : 0);
    const totalCentiseconds = Math.max(0, Math.floor(elapsed / 10));
    const seconds = Math.floor(totalCentiseconds / 100);
    const hours = Math.floor(seconds / 3600);
    const parts = hours > 0 ? [hours, Math.floor(seconds / 60) % 60, seconds % 60] : [Math.floor(seconds / 60), seconds % 60];
    q('[data-clock-whole]').textContent = parts.map(value => String(value).padStart(2, '0')).join(':');
    q('[data-clock-fraction]').textContent = `.${String(totalCentiseconds % 100).padStart(2, '0')}`;
  }
  function updatePeakIndicator() {
    const clipping = root.dataset.clipping === 'true';
    const label = clipping ? '当前输入或输出数字过载' : '当前未过载';
    q('[data-peak-indicator]').setAttribute('aria-label', label);
    q('[data-peak-indicator]').setAttribute('data-tooltip', label);
  }
  function renderRecording() {
    const active = ['recording', 'paused'].includes(recording.phase);
    root.dataset.recording = recording.phase;
    root.dataset.transportActive = String(active);
    updatePeakIndicator();
    q('[data-primary-label]').textContent = active ? '停止' : '录制';
    q('[data-action="primary"]').setAttribute('aria-label', active ? '停止录制' : '开始录制');
    q('[data-action="pause"]').hidden = !active;
    q('[data-action="pause"]').disabled = !active;
    const paused = recording.phase === 'paused';
    q('[data-primary-label]').hidden = paused;
    q('[data-resume-label]').hidden = !paused;
    q('[data-action="primary"]').setAttribute('data-tooltip', active ? '停止录制' : '开始录制');
    const pauseLabel = paused ? '继续录制' : '暂停录制';
    q('[data-action="pause"]').setAttribute('aria-label', pauseLabel);
    q('[data-action="pause"]').setAttribute('data-tooltip', pauseLabel);
    q('[data-action="pause"]').setAttribute('aria-pressed', String(paused));
    q('[data-pause-icon]').hidden = paused;
    q('[data-resume-icon]').hidden = !paused;
    updateClock();
  }
  function clockTick(now) {
    clockFrame = null;
    if (!root.isConnected || document.hidden || recording.phase !== 'recording') return;
    if (now - lastClockPaint >= 1000 / 30) {
      updateClock();
      lastClockPaint = now;
    }
    clockFrame = requestAnimationFrame(clockTick);
  }
  function syncClock() {
    if (clockFrame !== null) cancelAnimationFrame(clockFrame);
    clockFrame = null;
    updateClock();
    if (recording.phase === 'recording' && root.isConnected && !document.hidden) clockFrame = requestAnimationFrame(clockTick);
  }
  function animateTransport(from) {
    if (reducedMotion.matches) return;
    const button = q('[data-action="primary"]');
    const to = button.getBoundingClientRect();
    const dx = from.left - to.left, dy = from.top - to.top;
    if (Math.abs(dx) < 1 && Math.abs(dy) < 1) return;
    transportFlight = button.animate([
      { transform: `translate(${dx}px, ${dy}px)` },
      { transform: 'translate(0, 0)' }
    ], { duration: 560, easing: 'cubic-bezier(.16, 1, .3, 1)' });
    const flight = transportFlight;
    flight.onfinish = () => { if (transportFlight === flight) transportFlight = null; };
  }
  function performRecordingAction(action) {
    const wasActive = ['recording', 'paused'].includes(recording.phase);
    if (action === 'primary') action = ['recording', 'paused'].includes(recording.phase) ? 'stop' : 'record';
    const changesPlacement = action === 'record' || action === 'stop';
    const from = changesPlacement ? q('[data-action="primary"]').getBoundingClientRect() : null;
    const now = performance.now();
    let message;
    if (action === 'record' && ['idle', 'stopped'].includes(recording.phase)) {
      recording.elapsedMs = 0;
      recording.startedAt = now;
      recording.phase = 'recording';
      message = '演示录制已开始';
    } else if (action === 'pause' && recording.phase === 'recording') {
      recording.elapsedMs += now - recording.startedAt;
      recording.phase = 'paused';
      message = '演示录制已暂停';
    } else if (action === 'pause' && recording.phase === 'paused') {
      recording.startedAt = now;
      recording.phase = 'recording';
      message = '演示录制已继续';
    } else if (action === 'stop' && ['recording', 'paused'].includes(recording.phase)) {
      if (recording.phase === 'recording') recording.elapsedMs += now - recording.startedAt;
      recording.phase = 'stopped';
      message = '演示录制已停止';
    } else return;
    if (changesPlacement) { transportFlight?.cancel(); transportFlight = null; }
    renderRecording();
    syncClock();
    if (from && wasActive !== ['recording', 'paused'].includes(recording.phase)) animateTransport(from);
    announce(message);
  }
  q('[data-display-select]').addEventListener('change', event => {
    pitch.lastFrame = null;
    state.display = event.target.value;
    render(); save();
    announce(`已切换到${event.target.selectedOptions[0].textContent}`);
  });
  q('[data-input-toggle]').addEventListener('click', () => setInputOpen(!ui.inputOpen));
  q('[data-input-close]').addEventListener('click', () => setInputOpen(false));
  q('[data-save-path]').addEventListener('input', event => {
    file.path = event.target.value;
    file.directoryNameOnly = false;
    renderDirectoryFeedback();
  });
  q('[data-save-path]').addEventListener('change', save);
  q('[data-record-format]').addEventListener('change', event => {
    file.format = event.target.value;
    renderFile(); save();
    announce(`录制格式 ${file.format.toUpperCase()}${file.format === 'wav' ? '，无压缩品质选项' : '，可选择压缩品质'}`);
  });
  q('[data-compression-quality]').addEventListener('change', event => {
    if (!compressionOptions[file.format]?.some(([value]) => value === event.target.value)) return;
    file[file.format] = event.target.value;
    save(); announce(`已选择 ${event.target.selectedOptions[0].textContent}`);
  });
  q('[data-directory-browse]').addEventListener('click', async () => {
    if (typeof globalThis.showDirectoryPicker === 'function') {
      try {
        const directory = await globalThis.showDirectoryPicker({ mode: 'read' });
        chooseDirectory(directory.name);
        return;
      } catch (error) {
        if (error.name === 'AbortError') return;
      }
    }
    // Fallback for an embedded preview without File System Access support.
    // No selected file is opened, uploaded, or retained by the prototype.
    q('[data-directory-picker]').click();
  });
  q('[data-directory-picker]').addEventListener('change', event => {
    const selected = event.target.files[0];
    const name = selected?.webkitRelativePath?.split('/')[0];
    event.target.value = '';
    if (name) chooseDirectory(name);
  });
  root.addEventListener('keydown', event => {
    if (q('[data-calibration-dialog]').open) return;
    if (event.key === 'Escape' && ui.inputOpen) {
      event.preventDefault();
      setInputOpen(false);
    }
  });
  qa('[data-input-field]').forEach(select => select.addEventListener('change', event => {
    const key = event.target.dataset.inputField;
    const valid = key === 'device' ? ['umik1', 'default'].includes(event.target.value)
      : key === 'calibration' && (event.target.value === 'none' || calibrations.some(entry => entry.id === event.target.value));
    if (!valid) return;
    input[key] = event.target.value;
    renderInput(); save();
    announce(`已选择${event.target.selectedOptions[0].textContent}；当前为输入设置界面演示`);
  }));
  qa('[data-calibration-add]').forEach(button => button.addEventListener('click', () => {
    const picker = q('[data-calibration-picker]');
    const dialog = q('[data-calibration-dialog]');
    // Keep the native picker within the active modal when adding from there.
    (dialog.open ? dialog : q('.mm-input-fields')).append(picker);
    picker.click();
  }));
  q('[data-calibration-picker]').addEventListener('change', event => {
    const files = [...event.target.files];
    event.target.value = '';
    if (!files.length) return;
    let added = 0, skipped = 0;
    for (const file of files) {
      if (!/\.txt$/i.test(file.name) || file.name.length > 255) { skipped++; continue; }
      let entry = calibrations.find(item => item.name.toLowerCase() === file.name.toLowerCase());
      if (!entry) {
        if (calibrations.length >= 16) { skipped++; continue; }
        entry = { id: globalThis.crypto?.randomUUID?.() || `cal-${Date.now()}-${added}`, name: file.name };
        calibrations.push(entry);
      }
      calibrationFiles.set(entry.id, file);
      input.calibration = entry.id;
      added++;
    }
    qa('[data-calibration-feedback]').forEach(feedback => {
      feedback.hidden = skipped === 0;
      feedback.textContent = skipped ? '部分文件未加入：请选择 TXT 文件；演示列表最多保留 16 项。' : '';
    });
    renderInput(); renderCalibrationManager(); save();
    announce(added ? `已添加或更新 ${added} 份校准文件；尚未应用滤波` : '没有添加文件');
  });
  q('[data-calibration-manage]').addEventListener('click', () => {
    renderCalibrationManager();
    q('[data-calibration-dialog]').showModal();
  });
  q('[data-calibration-list]').addEventListener('click', event => {
    const button = event.target.closest('[data-remove-calibration]');
    if (!button) return;
    const id = button.dataset.removeCalibration;
    const entry = calibrations.find(item => item.id === id);
    if (!entry) return;
    calibrations = calibrations.filter(item => item.id !== id);
    calibrationFiles.delete(id);
    if (input.calibration === id) input.calibration = 'none';
    renderInput(); renderCalibrationManager(); save();
    const next = q('[data-calibration-list] button') || q('[data-calibration-dialog] [data-calibration-add]');
    next.focus({ preventScroll: true });
    announce(`已从列表移除 ${entry.name}；原始文件未删除`);
  });
  q('[data-calibration-dialog]').addEventListener('close', () => {
    if (ui.inputOpen) q('[data-calibration-manage]').focus({ preventScroll: true });
  });
  const gainInput = q('[data-gain-input]');
  let gainDrag = null;
  gainInput.addEventListener('pointerdown', event => {
    if (event.button !== 0) return;
    event.preventDefault();
    gainInput.focus({ preventScroll: true });
    gainDrag = { pointerId: event.pointerId, lastY: event.clientY, value: input.gainDb, initialValue: input.gainDb };
    gainInput.setPointerCapture(event.pointerId);
  });
  gainInput.addEventListener('pointermove', event => {
    if (!gainDrag || event.pointerId !== gainDrag.pointerId) return;
    const delta = (gainDrag.lastY - event.clientY) * (event.shiftKey ? .025 : .25);
    gainDrag.lastY = event.clientY;
    gainDrag.value = Math.max(-60, Math.min(24, gainDrag.value + delta));
    setGain(gainDrag.value);
  });
  function finishGainDrag(cancelled = false) {
    if (!gainDrag) return;
    const { pointerId, initialValue } = gainDrag;
    gainDrag = null;
    if (cancelled) setGain(initialValue);
    if (gainInput.hasPointerCapture(pointerId)) gainInput.releasePointerCapture(pointerId);
    save(); announce(`录音增益 ${formatGain(input.gainDb)}`);
  }
  gainInput.addEventListener('pointerup', () => finishGainDrag());
  gainInput.addEventListener('pointercancel', () => finishGainDrag(true));
  gainInput.addEventListener('lostpointercapture', () => finishGainDrag());
  gainInput.addEventListener('input', () => setGain(gainInput.valueAsNumber));
  gainInput.addEventListener('change', () => { save(); announce(`录音增益 ${formatGain(input.gainDb)}`); });
  gainInput.addEventListener('dblclick', () => { finishGainDrag(); setGain(0); save(); announce('录音增益已归零'); });
  gainInput.addEventListener('keydown', event => {
    if (event.key === 'Escape' && gainDrag) {
      event.preventDefault(); event.stopPropagation(); finishGainDrag(true); return;
    }
    const direction = { ArrowUp: 1, ArrowRight: 1, ArrowDown: -1, ArrowLeft: -1 }[event.key];
    if (direction || event.key === '0') {
      event.preventDefault();
      setGain(event.key === '0' ? 0 : input.gainDb + direction * (event.shiftKey ? .1 : 1));
      save(); announce(`录音增益 ${formatGain(input.gainDb)}`);
    }
  });
  qa('[data-action]').forEach(button => button.addEventListener('click', () => performRecordingAction(button.dataset.action)));
  q('[data-peak-reset]').addEventListener('click', () => {
    monitor.heldPeak = null;
    updateMeterReadouts();
    announce('峰值已清除，将从下一份监看数据重新累计');
  });
  function applyDesign() {
    root.style.colorScheme = design.appearance === '浅色' ? 'light' : 'dark';
    root.style.setProperty('--mm-gap', `${design.gap}px`);
    root.style.setProperty('--mm-radius', `${design.radius}px`);
    root.style.setProperty('--mm-accent', design.accent);
    requestAnimationFrame(draw);
  }
  if (globalThis.Tweak) {
    const tweak = new Tweak({ container: root, onChange: applyDesign });
    tweak.addSelect(design, 'appearance', { label: '外观', options: ['深色', '浅色'] });
    tweak.addSlider(design, 'gap', { label: '模块间距', min: 0, max: 14, step: 1, unit: 'px' });
    tweak.addSlider(design, 'radius', { label: '模块圆角', min: 0, max: 16, step: 2, unit: 'px' });
    tweak.addColorPicker(design, 'accent', { label: '曲线与指针', reference: '--mm-accent' });
  }
  globalThis.addEventListener('openai:set_globals', event => {
    const widgetState = event.detail?.globals?.widgetState;
    const data = widgetState?.modelContent;
    if (!data) return;
    if (['level', 'spectrum', 'pitch'].includes(data.display)) state.display = data.display;
    restoreCalibrations(widgetState?.privateContent?.calibrations);
    restoreInput(data.input);
    restoreFile(data.file, widgetState?.privateContent?.file);
    if (typeof widgetState?.privateContent?.inputOpen === 'boolean') ui.inputOpen = widgetState.privateContent.inputOpen;
    render();
  });
  new ResizeObserver(draw).observe(q('.mm-board'));
  document.addEventListener('visibilitychange', () => { syncMonitoring(); syncClock(); });
  reducedMotion.addEventListener('change', () => {
    if (reducedMotion.matches) { transportFlight?.cancel(); transportFlight = null; }
    syncMonitoring();
  });
  render();
  renderRecording();
})();
