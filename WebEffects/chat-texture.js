import { CanvasMessage, transitions } from "./message-motion.js";

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
  let motion = {};

  function measure(row, rect) {
    const style = getComputedStyle(row);
    const key = [row.offsetWidth, style.padding, style.borderLeftWidth, font(style),
      style.color, row.firstElementChild?.style.color, row.textContent].join("|");
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
    return { runs: entry.runs, images, entry, width: rect.width, height: rect.height,
      radius: parseFloat(style.borderTopLeftRadius) || 0, background: style.backgroundColor,
      borderWidth: parseFloat(style.borderLeftWidth) || 0, borderColor: style.borderLeftColor };
  }

  function paintMessageBackground(ctx, { width, height, radius, background, borderWidth, borderColor }) {
    const corner = Math.min(radius, width / 2, height / 2);
    if (background !== "rgba(0, 0, 0, 0)") {
      ctx.beginPath();
      ctx.roundRect(0, 0, width, height, corner);
      ctx.fillStyle = background;
      ctx.fill();
    }
    if (borderWidth > 0) {
      const inset = borderWidth / 2;
      ctx.beginPath();
      ctx.roundRect(inset, inset, width - borderWidth, height - borderWidth, Math.max(0, corner - inset));
      ctx.lineWidth = borderWidth;
      ctx.strokeStyle = borderColor;
      ctx.stroke();
    }
  }

  function paintContent(ctx, content) {
    for (const run of content.runs) {
      ctx.font = run.font;
      ctx.fillStyle = run.color;
      const metrics = ctx.measureText(run.text);
      ctx.fillText(run.text, run.x, run.y + (run.height + metrics.fontBoundingBoxAscent - metrics.fontBoundingBoxDescent) / 2);
    }
    for (const { image, x, y, width, height } of content.images)
      ctx.drawImage(image, x, y, width, height);
  }

  function fragmentTexture(ctx, content, { particleSize, particleLimit, particleDirection, particleCone }) {
    const key = [particleSize, particleLimit, particleDirection, particleCone, content.height, ...content.images.map(({ image, x, y, width, height }) =>
      [image.src, x, y, width, height].join(","))].join("|");
    if (content.entry.fragments?.key === key) return content.entry.fragments.value;

    let right = 0;
    for (const run of content.runs) {
      ctx.font = run.font;
      const metrics = ctx.measureText(run.text);
      right = Math.max(right, run.x + Math.max(metrics.width, metrics.actualBoundingBoxRight));
    }
    for (const image of content.images) right = Math.max(right, image.x + image.width);
    const width = Math.min(4096, Math.max(1, Math.ceil(right + 2)));
    const height = Math.min(2048, Math.max(1, Math.ceil(content.height + 2)));
    const texture = document.createElement("canvas");
    texture.width = width;
    texture.height = height;
    const surface = texture.getContext("2d", { willReadFrequently: true });
    paintContent(surface, content);

    let value = null;
    try {
      const pixels = surface.getImageData(0, 0, width, height).data;
      // Bound the number of draw calls for very large text or wrapped messages.
      let step = Math.max(particleSize, Math.ceil(Math.sqrt(width * height / particleLimit)));
      while (Math.ceil(width / step) * Math.ceil(height / step) > particleLimit) step++;
      const radians = Math.PI / 180;
      const tiles = [];
      for (let y = 0; y < height; y += step) {
        for (let x = 0; x < width; x += step) {
          const w = Math.min(step, width - x);
          const h = Math.min(step, height - y);
          let ink = false;
          for (let dy = 0; dy < h && !ink; dy++) {
            for (let dx = 0; dx < w; dx++) {
              if (pixels[((y + dy) * width + x + dx) * 4 + 3] > 12) {
                ink = true;
                break;
              }
            }
          }
          if (!ink) continue;
          const seed = Math.imul(x + 1, 73856093) ^ Math.imul(y + 1, 19349663);
          const fraction = (seed >>> 0) / 0x100000000;
          const angle = (particleDirection + (fraction - 0.5) * particleCone) * radians;
          const speed = 0.35 + ((seed >>> 8) & 255) / 255 * 0.65;
          tiles.push({ x, y, w, h, vx: Math.cos(angle) * speed, vy: Math.sin(angle) * speed });
        }
      }
      value = { texture, tiles };
    } catch (error) {
      if (error.name !== "SecurityError") throw error;
    }
    content.entry.fragments = { key, value };
    return value;
  }

  function drawRow(ctx, content, position) {
    if (position.opacity <= 0 || position.scale <= 0 || position.scatter >= 1) return;
    ctx.save();
    ctx.globalAlpha = position.opacity;
    ctx.translate(position.x, position.y);
    ctx.scale(position.scale, position.scale);
    if (position.scatter > 0) ctx.globalAlpha *= 1 - position.scatter ** 2;
    paintMessageBackground(ctx, content);
    if (position.scatter > 0 && position.particles) {
      const { particleSpread } = position.particles;
      const fragments = fragmentTexture(ctx, content, position.particles);
      if (fragments) {
        const distance = position.scatter * particleSpread;
        for (const { x, y, w, h, vx, vy } of fragments.tiles)
          ctx.drawImage(fragments.texture, x, y, w, h, x + vx * distance, y + vy * distance, w, h);
      } else paintContent(ctx, content);
    } else paintContent(ctx, content);
    ctx.restore();
  }

  function target(rect) {
    return { x: rect.left, y: rect.top };
  }

  function moveProfile(bounds) {
    return transitions(0, motion.movement,
      motion.moveDurationMs ?? 280, 0, bounds, motion.moveCurve).find(effect => effect.kind === 0);
  }

  function update(render, theme) {
    const now = time;
    motion = theme.messageMotion ?? {};
    const duration = Math.max(100, Math.min(2000, theme.messageTransitionMs ?? 280));
    const direction = theme.newestAtTop ? 1 : -1;

    const hasEffects = [motion.entrance, motion.departure, motion.movement]
      .some(profile => profile?.items?.some(item => item.enabled));
    if (!hasEffects) {
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
        existing.moveTo(target(bounds), moveProfile(bounds), now);
        continue;
      }
      const previous = existing?.position(now);
      const message = new CanvasMessage(target(bounds));
      const distance = -direction * (bounds.height + (parseFloat(getComputedStyle(row).marginTop) || 0));
      const profile = transitions(0, motion.entrance, duration, distance, bounds);
      message.enter(profile, now, existing?.state === "removed" ? null : previous);
      messages.set(id, message);
    }

    for (const [id, { bounds, content }] of before) {
      const message = messages.get(id);
      const position = message.position(now);
      const distance = direction < 0 ? -position.y - bounds.height : window.innerHeight - position.y;
      const profile = transitions(0, motion.departure, duration, distance, bounds);
      if (profile.length) message.exit(profile, now, content);
      else messages.delete(id);
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
      if (message && (message.target.x !== logical.x || message.target.y !== logical.y))
        message.moveTo(logical, moveProfile(bounds), now);
      const position = message?.position(now) ?? { ...logical, opacity: 1, scale: 1 };
      rows.push({ content: measure(row, bounds), position, moving: message?.moving === true });
    }

    ctx.save();
    const rect = chat.getBoundingClientRect();
    ctx.beginPath();
    ctx.rect(rect.left, rect.top, rect.width, rect.height);
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
