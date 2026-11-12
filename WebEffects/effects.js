import { EffectLayer } from "./effect-layer.js";
import { chatTexture, drawMedia } from "./chat-texture.js";

const seconds = (min, max) => {
  const start = Math.min(min, max);
  return start + Math.random() * (Math.max(min, max) - start);
};
const applies = (effect, target) => effect?.enabled && (effect.target === target || effect.target === 2) ? effect : null;

export function createObsEffects(chat, background) {
  const layers = new Map();
  const chatScene = chatTexture(chat);
  let frameId = 0;
  let lastFrame = 0;
  let glitchConfig;
  let settingsKey;
  let mediaReady;
  let mediaElement;
  let disposed = false;
  let pulse = { until: 0, strong: false };
  let randomAt = Infinity;
  function invalidateChat() {
    const layer = layers.get(0);
    if (layer) layer.dirty = true;
  }
  chat.addEventListener("load", invalidateChat, true);
  chat.addEventListener("obs-content-changed", invalidateChat);

  function burst(now) {
    pulse = {
      until: now + seconds(glitchConfig.minDuration, glitchConfig.maxDuration),
      strong: Math.random() * 100 < 100 - glitchConfig.threshold
    };
    if (glitchConfig.trigger === 1)
      randomAt = pulse.until + seconds(glitchConfig.minInterval, glitchConfig.maxInterval);
  }

  function setLayer(target, zIndex, draw, settings) {
    const config = {
      chromatic: applies(settings.chromatic, target),
      glitch: applies(settings.glitch, target),
      scanlines: applies(settings.scanlines, target)
    };
    let layer = layers.get(target);
    if (!layer) {
      layer = new EffectLayer(zIndex, draw, fail);
      layers.set(target, layer);
    }
    layer.configure(config);
  }

  function frame(now) {
    frameId = 0;
    if (disposed || layers.size === 0) return;
    const elapsed = now - lastFrame;
    const moving = chatScene.moving();
    if (elapsed >= (moving ? 15 : 30)) {
      lastFrame = now;
      if (now >= randomAt) burst(now);
      const width = Math.max(1, window.innerWidth);
      const height = Math.max(1, window.innerHeight);
      const ratio = Math.min(2, window.devicePixelRatio || 1, 3840 / Math.max(width, height));
      const delta = Math.min(0.1, elapsed / 1000);
      const chatLayer = layers.get(0);
      const mediaLayer = layers.get(1);
      try {
        if (moving) chatScene.advance(elapsed);
        chatLayer?.frame(width, height, ratio, now, delta, pulse, moving);
        if (mediaLayer) {
          const element = background.firstElementChild;
          const ready = (element?.videoWidth || element?.naturalWidth || 0) > 0;
          const video = element instanceof HTMLVideoElement && ready && !element.paused;
          mediaLayer.frame(width, height, ratio, now, delta, pulse, video || ready !== mediaReady || element !== mediaElement);
          mediaReady = ready;
          mediaElement = element;
        }
      } catch (error) {
        fail(error);
        return;
      }
    }
    frameId = requestAnimationFrame(frame);
  }

  function dispose() {
    disposed = true;
    chat.removeEventListener("load", invalidateChat, true);
    chat.removeEventListener("obs-content-changed", invalidateChat);
    cancelAnimationFrame(frameId);
    for (const layer of layers.values()) layer.dispose();
    layers.clear();
  }

  function fail(error) {
    console.warn("APEX Chatd: WebGL effects unavailable", error);
    dispose();
  }

  function update(effects, newMessage) {
    if (disposed) return;
    try {
      const items = effects?.items ?? [];
      const settings = {
        chromatic: items.includes(0) && effects.chromatic?.enabled ? effects.chromatic : null,
        glitch: items.includes(1) && effects.glitch?.enabled ? effects.glitch : null,
        scanlines: items.includes(2) && effects.scanlines?.enabled ? effects.scanlines : null
      };
      const key = JSON.stringify(settings);
      if (settingsKey !== key) {
        const nextGlitch = settings.glitch;
        if (JSON.stringify(nextGlitch) !== JSON.stringify(glitchConfig)) {
          glitchConfig = nextGlitch;
          pulse = { until: 0, strong: false };
          randomAt = glitchConfig?.trigger === 1
            ? performance.now() + seconds(glitchConfig.minInterval, glitchConfig.maxInterval) : Infinity;
        }
        setLayer(0, 2, chatScene.draw, settings);
        setLayer(1, 0, (ctx, w, h) => drawMedia(ctx, background, w, h), settings);
        settingsKey = key;
      }
      if (newMessage && glitchConfig?.trigger === 0) burst(performance.now());
      for (const layer of layers.values()) layer.dirty = true;
      if (!frameId && layers.size) frameId = requestAnimationFrame(frame);
    } catch (error) {
      fail(error);
    }
  }

  return { update, updateChat: chatScene.update, dispose };
}
