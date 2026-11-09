const neutral = { x: 0, y: 0, opacity: 1, scale: 1 };
const curves = [
  t => t,
  t => t ** 3,
  t => 1 - (1 - t) ** 3,
  t => t * t * (3 - 2 * t)
];

export function transition(kind, profile, fallbackDuration, distance, bounds) {
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const edge = { ...neutral };
  const anchor = { x: 0, y: 0 };
  if (kind === 1) {
    edge.scale = clamp(profile?.scale ?? 0, 0, 200) / 100;
    anchor.x = bounds.width * clamp(profile?.originX ?? 0, 0, 100) / 100;
    anchor.y = bounds.height * clamp(profile?.originY ?? 0, 0, 100) / 100;
  } else if (kind === 2) {
    edge.opacity = clamp(profile?.opacity ?? 0, 0, 100) / 100;
  } else {
    edge.x = clamp(profile?.offsetX ?? 0, -500, 500);
    edge.y = profile ? clamp(profile.offsetY ?? 0, -500, 500) : distance;
  }
  return { kind, edge, anchor, duration: clamp(profile?.durationMs ?? fallbackDuration, 100, 5000),
    delay: clamp(profile?.delayMs ?? 0, 0, 2000), curve: profile?.curve ?? 3 };
}

function tween(from, to, start, duration, curve = 3) {
  return { from, to, start, duration, ease: curves[curve] ?? curves[3] };
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

export class CanvasMessage {
  constructor(target) {
    this.target = target;
    this.state = "visible";
  }

  enter(profile, now, previous = null) {
    this.state = "entering";
    const { edge, anchor, duration, delay, curve } = profile;
    const from = previous ? {
      ...previous, x: previous.x - this.target.x, y: previous.y - this.target.y
    } : { ...edge, x: edge.x + (1 - edge.scale) * anchor.x, y: edge.y + (1 - edge.scale) * anchor.y };
    this.appearance = tween(from, neutral, now + delay, duration, curve);
  }

  moveTo(target, duration, curve, now) {
    if (target.x === this.target.x && target.y === this.target.y) return;
    const current = this.movement ? sample(this.movement, now) : this.target;
    this.target = target;
    this.movement = duration > 0 ? tween(current, target, now, duration, curve) : null;
  }

  exit(profile, now, content) {
    const from = this.position(now);
    const { kind, edge, anchor, duration, delay, curve } = profile;
    const scale = kind === 1 ? edge.scale : from.scale;
    this.state = "exiting";
    this.content = content;
    this.departure = tween(from, {
      x: from.x + edge.x + (from.scale - scale) * anchor.x,
      y: from.y + edge.y + (from.scale - scale) * anchor.y,
      scale, opacity: kind === 2 ? edge.opacity : from.opacity
    }, now + delay, duration, curve);
    this.movement = null;
    this.appearance = null;
  }

  position(now) {
    if (this.departure) {
      const position = sample(this.departure, now);
      if (position === this.departure.to) this.state = "removed";
      return position;
    }

    const layout = this.movement ? sample(this.movement, now) : this.target;
    const appearance = this.appearance ? sample(this.appearance, now) : neutral;
    if (layout === this.target) this.movement = null;
    if (appearance === neutral) {
      this.appearance = null;
      this.state = "visible";
    }
    return { x: layout.x + appearance.x, y: layout.y + appearance.y,
      opacity: appearance.opacity, scale: appearance.scale };
  }

  get moving() {
    return !!(this.movement || this.appearance || this.state === "exiting");
  }
}
