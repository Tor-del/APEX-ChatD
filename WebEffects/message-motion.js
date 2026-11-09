const neutral = { x: 0, y: 0, opacity: 1, scale: 1, scatter: 0 };
const curves = [
  t => t,
  t => t ** 3,
  t => 1 - (1 - t) ** 3,
  t => t * t * (3 - 2 * t)
];

export function transitions(fallback, profile, duration, distance, bounds, curve = 3) {
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const mask = profile?.effects ?? fallback;
  const items = profile?.items ?? [0, 1, 2].filter(kind => mask & (1 << kind))
    .map(kind => ({ kind, enabled: true, options: profile }));
  return items.filter(item => item.enabled).map(({ kind, options }) => ({
    kind,
    x: clamp(options?.offsetX ?? 0, -500, 500),
    y: options ? clamp(options.offsetY ?? 0, -500, 500) : distance,
    scale: clamp(options?.scale ?? 0, 0, 200) / 100,
    opacity: clamp(options?.opacity ?? 0, 0, 100) / 100,
    anchorX: bounds.width * clamp(options?.originX ?? 0, 0, 100) / 100,
    anchorY: bounds.height * clamp(options?.originY ?? 0, 0, 100) / 100,
    particleSize: clamp(options?.particleSize ?? 4, 2, 12),
    particleSpread: clamp(options?.particleSpread ?? 80, 16, 300),
    particleLimit: clamp(options?.particleLimit ?? 1200, 50, 2000),
    particleDirection: clamp(options?.particleDirection ?? 0, -180, 180),
    particleCone: clamp(options?.particleCone ?? 360, 0, 360),
    duration: clamp(options?.durationMs ?? duration, 100, 5000),
    delay: clamp(options?.delayMs ?? 0, 0, 2000),
    ease: curves[options?.curve ?? curve] ?? curves[3]
  }));
}

function tween(from, to, start, duration, ease) {
  return { from, to, start, duration, ease };
}

function sample(track, now) {
  const t = Math.max(0, Math.min(1, (now - track.start) / track.duration));
  if (t === 1) return track.to;
  const eased = track.ease(t);
  const value = {};
  for (const key of Object.keys(track.to))
    value[key] = track.from[key] + (track.to[key] - track.from[key]) * eased;
  return value;
}

function endpoints(effect, base) {
  if (effect.kind === 0) return [{ x: 0, y: 0 }, { x: effect.x, y: effect.y }];
  if (effect.kind === 1) return [{ scale: base.scale, x: 0, y: 0 }, {
    scale: effect.scale, x: (base.scale - effect.scale) * effect.anchorX,
    y: (base.scale - effect.scale) * effect.anchorY
  }];
  if (effect.kind === 3) return [{ scatter: base.scatter }, { scatter: 1 }];
  return [{ opacity: base.opacity }, { opacity: effect.opacity }];
}

function combine(base, components) {
  const result = { ...base };
  for (const part of components) {
    result.x += part.x ?? 0;
    result.y += part.y ?? 0;
    result.scale = part.scale ?? result.scale;
    result.opacity = part.opacity ?? result.opacity;
    result.scatter = part.scatter ?? result.scatter;
  }
  return result;
}

export class CanvasMessage {
  constructor(target) {
    this.target = target;
    this.state = "visible";
  }

  enter(effects, now, previous = null) {
    if (!effects.length) return;
    this.state = "entering";
    this.particles = previous?.particles ?? effects.find(effect => effect.kind === 3);
    if (previous) {
      const duration = Math.max(...effects.map(effect => effect.delay + effect.duration));
      this.appearance = [tween({ ...previous, x: previous.x - this.target.x, y: previous.y - this.target.y },
        neutral, now, duration, curves[3])];
      return;
    }
    this.appearance = effects.map(effect => {
      const [from, edge] = endpoints(effect, neutral);
      return tween(edge, from, now + effect.delay, effect.duration, effect.ease);
    });
  }

  moveTo(target, slide, now) {
    if (target.x === this.target.x && target.y === this.target.y) return;
    const current = this.movement ? sample(this.movement, now) : this.target;
    this.target = target;
    this.movement = slide ? tween(current, target, now + slide.delay, slide.duration, slide.ease) : null;
  }

  exit(effects, now, content) {
    const base = this.position(now);
    this.particles = effects.find(effect => effect.kind === 3) ?? this.particles;
    this.state = "exiting";
    this.content = content;
    this.departure = { base, tracks: effects.map(effect => {
      const [from, edge] = endpoints(effect, base);
      return tween(from, edge, now + effect.delay, effect.duration, effect.ease);
    }) };
    this.movement = null;
    this.appearance = null;
  }

  position(now) {
    if (this.departure) {
      const { base, tracks } = this.departure;
      const parts = tracks.map(track => sample(track, now));
      if (tracks.every((track, index) => parts[index] === track.to)) this.state = "removed";
      return { ...combine(base, parts), particles: this.particles };
    }

    const layout = this.movement ? sample(this.movement, now) : this.target;
    if (layout === this.target) this.movement = null;
    let appearance = neutral;
    if (this.appearance) {
      const parts = this.appearance.map(track => sample(track, now));
      appearance = combine(neutral, parts);
      if (this.appearance.every((track, index) => parts[index] === track.to)) {
        this.appearance = null;
        this.state = "visible";
      }
    }
    return { x: layout.x + appearance.x, y: layout.y + appearance.y,
      opacity: appearance.opacity, scale: appearance.scale, scatter: appearance.scatter, particles: this.particles };
  }

  get moving() {
    return !!(this.movement || this.appearance || this.state === "exiting");
  }
}
