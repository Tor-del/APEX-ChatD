import { CanvasMessage, transition } from "./message-motion.js";

const segmenter = new Intl.Segmenter(undefined, { granularity: "grapheme" });

function font(style) {
  return [style.fontStyle, style.fontWeight, style.fontSize, style.fontFamily].join(" ");
}

function readRuns(row, bounds) {
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
      const y = rect.top - bounds.top;
      if (current && Math.abs(current.y - y) < 0.5) current.text += segment;
      else {
        current = { text: segment, x: rect.left - bounds.left, y,
          height: rect.height, font: font(style), color: style.color };
        runs.push(current);
      }
    }
  }
  return runs;
}

export function chatTexture(chat) {
  const cache = new WeakMap();
  const messages = new Map();
  let time = 0;
  let moveDuration = 0;
  let moveCurve;

  function measure(row, rect) {
    const style = getComputedStyle(row);
    const key = [row.offsetWidth, font(style), style.color, row.firstElementChild?.style.color, row.textContent].join("|");
    let entry = cache.get(row);
    if (entry?.key !== key) {
      entry = { key, runs: readRuns(row, rect) };
      cache.set(row, entry);
    }
    const images = [];
    for (const image of row.querySelectorAll('img.emote')) {
      if (!image.complete || !image.naturalWidth || !image.naturalHeight) continue;
      const box = image.getBoundingClientRect();
      const fit = Math.min(box.width / image.naturalWidth, box.height / image.naturalHeight);
      const width = image.naturalWidth * fit;
      const height = image.naturalHeight * fit;
      images.push({ image, x: box.left - rect.left + (box.width - width) / 2,
        y: box.top - rect.top + (box.height - height) / 2, width, height });
    }
    return { runs: entry.runs, images };
  }

  function drawRow(ctx, content, position) {
    if (position.opacity <= 0 || position.scale <= 0) return;
    ctx.save();
    ctx.globalAlpha = position.opacity;
    ctx.translate(position.x, position.y);
    ctx.scale(position.scale, position.scale);
    for (const run of content.runs) {
      ctx.font = run.font;
      ctx.fillStyle = run.color;
      const metrics = ctx.measureText(run.text);
      ctx.fillText(run.text, run.x, run.y + (run.height + metrics.fontBoundingBoxAscent - metrics.fontBoundingBoxDescent) / 2);
    }
    for (const { image, x, y, width, height } of content.images)
      ctx.drawImage(image, x, y, width, height);
    ctx.restore();
  }

  function target(rect) {
    return { x: rect.left, y: rect.top };
  }

  function update(render, theme) {
    const now = time;
    const motion = theme.messageMotion ?? {};
    const duration = Math.max(100, Math.min(2000, theme.messageTransitionMs ?? 280));
    moveDuration = !theme.animateMessages || motion.animateMove === false
      ? 0 : Math.max(100, Math.min(2000, motion.moveDurationMs ?? 280));
    moveCurve = motion.moveCurve;
    const direction = theme.newestAtTop ? 1 : -1;

    if (!theme.animateMessages) {
      messages.clear();
      render();
      return;
    }

    const before = new Map();
    for (const row of chat.children) {
      const bounds = row.getBoundingClientRect();
      const id = row.dataset.id;
      if (!messages.has(id)) messages.set(id, new CanvasMessage(target(bounds)));
      before.set(id, { bounds, content: measure(row, bounds) });
    }

    render();
    for (const row of chat.children) {
      const bounds = row.getBoundingClientRect();
      const id = row.dataset.id;
      const existing = messages.get(id);
      before.delete(id);
      if (existing && existing.state !== "exiting") {
        existing.moveTo(target(bounds), moveDuration, moveCurve, now);
        continue;
      }
      const previous = existing?.position(now);
      const message = new CanvasMessage(target(bounds));
      const distance = -direction * (bounds.height + (parseFloat(getComputedStyle(row).marginTop) || 0));
      const profile = transition(motion.enter ?? 0, motion.entrance, duration, distance, bounds);
      message.enter(profile, now, existing?.state === "removed" ? null : previous);
      messages.set(id, message);
    }

    for (const [id, { bounds, content }] of before) {
      const message = messages.get(id);
      const position = message.position(now);
      const distance = direction < 0 ? -position.y - bounds.height : window.innerHeight - position.y;
      const profile = transition(motion.exit ?? 2, motion.departure, duration, distance, bounds);
      message.exit(profile, now, content);
    }

    const exiting = [...messages].filter(([, message]) => message.state === "exiting");
    for (const [id] of exiting.slice(0, Math.max(0, exiting.length - 50)))
      messages.delete(id);
  }

  function draw(ctx) {
    const now = time;
    for (const [id, message] of messages) {
      if (message.state !== "exiting") continue;
      const position = message.position(now);
      if (message.state === "removed") messages.delete(id);
      else drawRow(ctx, message.content, position);
    }

    const rows = [];
    for (const row of chat.children) {
      const bounds = row.getBoundingClientRect();
      if (!bounds.width || !bounds.height) continue;
      const message = messages.get(row.dataset.id);
      const logical = target(bounds);
      // Layout may change between snapshots, for example after a viewport resize.
      // Retarget from the current visual position without cancelling the transition.
      message?.moveTo(logical, moveDuration, moveCurve, now);
      const position = message?.position(now) ?? { ...logical, opacity: 1, scale: 1 };
      rows.push({ content: measure(row, bounds), position, moving: message?.moving === true });
    }

    ctx.save();
    const rect = chat.getBoundingClientRect();
    const style = getComputedStyle(chat);
    ctx.beginPath();
    ctx.roundRect(rect.left, rect.top, rect.width, rect.height, parseFloat(style.borderTopLeftRadius) || 0);
    ctx.clip();
    for (const row of rows) if (!row.moving) drawRow(ctx, row.content, row.position);
    ctx.restore();
    for (const row of rows) if (row.moving) drawRow(ctx, row.content, row.position);
  }

  function advance(elapsed) {
    // A stalled frame must not consume the entire transition before it can be drawn.
    time += Math.min(50, elapsed);
  }

  return { update, draw, advance, moving: () => [...messages.values()].some(message => message.moving) };
}



export function drawMedia(ctx, container, width, height) {
  ctx.fillStyle = getComputedStyle(container).backgroundColor;
  ctx.fillRect(0, 0, width, height);
  const element = container.firstElementChild;
  const sourceWidth = element?.videoWidth || element?.naturalWidth;
  const sourceHeight = element?.videoHeight || element?.naturalHeight;
  if (!sourceWidth || !sourceHeight) return;
  const ratio = Math.max(width / sourceWidth, height / sourceHeight);
  const w = sourceWidth * ratio;
  const h = sourceHeight * ratio;
  ctx.drawImage(element, (width - w) / 2, (height - h) / 2, w, h);
}
