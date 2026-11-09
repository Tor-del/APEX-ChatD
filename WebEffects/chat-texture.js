const segmenter = new Intl.Segmenter(undefined, { granularity: "grapheme" });

function font(style) {
  return [style.fontStyle, style.fontWeight, style.fontSize, style.fontFamily].join(" ");
}

function readRuns(row, bounds, scaleX, scaleY) {
  const runs = [];
  const range = document.createRange();
  for (const child of row.childNodes) {
    const node = child.nodeType === Node.TEXT_NODE ? child : child.firstChild;
    if (!node || node.nodeType !== Node.TEXT_NODE) continue;

    const style = getComputedStyle(child.nodeType === Node.TEXT_NODE ? row : child);
    let current;
    for (const { segment, index } of segmenter.segment(node.data)) {
      range.setStart(node, index);
      range.setEnd(node, index + segment.length);
      const rect = range.getBoundingClientRect();
      if (!rect.height) continue;
      const y = (rect.top - bounds.top) / scaleY;
      if (current && Math.abs(current.y - y) < 0.5) current.text += segment;
      else {
        current = { text: segment, x: (rect.left - bounds.left) / scaleX, y,
          height: rect.height / scaleY, font: font(style), color: style.color };
        runs.push(current);
      }
    }
  }
  return runs;
}

export function chatTexture(chat, departures) {
  const cache = new WeakMap();
  function drawRows(ctx, parent) {
    for (const row of parent.children) {
      const rect = row.getBoundingClientRect();
      if (!row.offsetWidth || !row.offsetHeight || !rect.width || !rect.height) continue;
      const style = getComputedStyle(row);
      const opacity = Number(style.opacity);
      if (opacity <= 0) continue;
      const matrix = style.transform === "none" ? null : new DOMMatrixReadOnly(style.transform);
      const scaleX = matrix?.a ?? 1;
      const scaleY = matrix?.d ?? 1;
      if (scaleX <= 0 || scaleY <= 0) continue;

      const key = [row.offsetWidth, font(style), style.color, row.firstElementChild?.style.color, row.textContent].join("|");
      let entry = cache.get(row);
      if (entry?.key !== key) {
        entry = { key, runs: readRuns(row, rect, scaleX, scaleY) };
        cache.set(row, entry);
      }

      ctx.save();
      ctx.globalAlpha = opacity;
      ctx.translate(rect.left, rect.top);
      ctx.scale(scaleX, scaleY);
      for (const run of entry.runs) {
        ctx.font = run.font;
        ctx.fillStyle = run.color;
        const metrics = ctx.measureText(run.text);
        const ascent = metrics.fontBoundingBoxAscent;
        const descent = metrics.fontBoundingBoxDescent;
        ctx.fillText(run.text, run.x, run.y + (run.height + ascent - descent) / 2);
      }
      for (const image of row.querySelectorAll('img.emote')) {
        if (!image.complete || !image.naturalWidth || !image.naturalHeight) continue;
        const box = image.getBoundingClientRect();
        const width = box.width / scaleX;
        const height = box.height / scaleY;
        const fit = Math.min(width / image.naturalWidth, height / image.naturalHeight);
        const w = image.naturalWidth * fit;
        const h = image.naturalHeight * fit;
        ctx.drawImage(image, (box.left - rect.left) / scaleX + (width - w) / 2,
          (box.top - rect.top) / scaleY + (height - h) / 2, w, h);
      }
      ctx.restore();
    }
  }

  return ctx => {
    const rect = chat.getBoundingClientRect();
    const style = getComputedStyle(chat);
    ctx.save();
    ctx.beginPath();
    ctx.roundRect(rect.left, rect.top, rect.width, rect.height, parseFloat(style.borderTopLeftRadius) || 0);
    ctx.clip();
    ctx.fillStyle = style.backgroundColor;
    ctx.fillRect(rect.left, rect.top, rect.width, rect.height);
    drawRows(ctx, chat);
    ctx.restore();
    drawRows(ctx, departures);
  };
}

export function drawMedia(ctx, container, width, height) {
  const element = container.firstElementChild;
  const sourceWidth = element?.videoWidth || element?.naturalWidth;
  const sourceHeight = element?.videoHeight || element?.naturalHeight;
  if (!sourceWidth || !sourceHeight) return;
  const ratio = Math.max(width / sourceWidth, height / sourceHeight);
  const w = sourceWidth * ratio;
  const h = sourceHeight * ratio;
  ctx.drawImage(element, (width - w) / 2, (height - h) / 2, w, h);
}
