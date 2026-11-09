import { CanvasTexture, Mesh, MeshBasicMaterial, NoBlending, OrthographicCamera, PlaneGeometry, Scene, SRGBColorSpace, WebGLRenderer } from "three";
import { ChromaticAberrationEffect, EffectComposer, EffectPass, GlitchEffect, GlitchMode, RenderPass, ScanlineEffect } from "postprocessing";

export class EffectLayer {
  constructor(sources, zIndex, draw, failed) {
    this.sources = sources;
    this.draw = draw;
    this.textureCanvas = document.createElement("canvas");
    this.ctx = this.textureCanvas.getContext("2d");
    this.renderer = new WebGLRenderer({ alpha: true, premultipliedAlpha: false, antialias: false,
      powerPreference: "high-performance", depth: false, stencil: false });
    this.renderer.setClearColor(0, 0);
    const canvas = this.renderer.domElement;
    canvas.className = "obs-effects-layer";
    canvas.dataset.effectsLayer = zIndex === 2 ? "chat" : "background";
    canvas.style.zIndex = zIndex;
    this.contextLost = event => { event.preventDefault(); failed(); };
    canvas.addEventListener("webglcontextlost", this.contextLost);
    this.texture = new CanvasTexture(this.textureCanvas);
    this.texture.colorSpace = SRGBColorSpace;
    this.texture.generateMipmaps = false;
    this.scene = new Scene();
    this.camera = new OrthographicCamera(-1, 1, 1, -1, 0, 2);
    this.camera.position.z = 1;
    this.mesh = new Mesh(new PlaneGeometry(2, 2), new MeshBasicMaterial({ map: this.texture, toneMapped: false, transparent: true, blending: NoBlending }));
    this.scene.add(this.mesh);
    document.body.append(canvas);
    this.dirty = true;
  }

  configure(config) {
    this.config = config;
    const key = [!!config.chromatic, !!config.glitch, config.glitch?.noiseSize, !!config.scanlines].join("/");
    if (key !== this.key) {
      this.composer?.dispose();
      this.composer = new EffectComposer(this.renderer, { depthBuffer: false, stencilBuffer: false });
      this.composer.addPass(new RenderPass(this.scene, this.camera));
      this.glitch = config.glitch ? new GlitchEffect({ dtSize: config.glitch.noiseSize }) : null;
      if (this.glitch) this.composer.addPass(new EffectPass(this.camera, this.glitch));
      this.chromatic = config.chromatic || config.glitch ? new ChromaticAberrationEffect() : null;
      this.lines = config.scanlines ? new ScanlineEffect() : null;
      const colorEffects = [this.chromatic, this.lines].filter(Boolean);
      if (colorEffects.length) this.composer.addPass(new EffectPass(this.camera, ...colorEffects));
      this.key = key;
      this.width = undefined;
    }

    if (this.glitch) {
      this.glitch.minStrength = config.glitch.weak / 100;
      this.glitch.maxStrength = config.glitch.strong / 100;
      this.glitch.columns = config.glitch.columns / 100;
    }
    if (this.chromatic) {
      this.chromatic.radialModulation = config.chromatic?.radial ?? false;
      this.chromatic.modulationOffset = (config.chromatic?.modulation ?? 0) / 100;
      this.chromatic.blendMode.opacity.value = (config.chromatic?.intensity ?? 100) / 100;
    }
    if (this.lines) {
      this.lines.density = config.scanlines.density / 100;
      this.lines.scrollSpeed = config.scanlines.speed / 100;
      this.lines.blendMode.opacity.value = config.scanlines.intensity / 100;
    }
    this.dirty = true;
  }

  frame(width, height, ratio, time, delta, pulse, animatedSource) {
    const resized = width !== this.width || height !== this.height || ratio !== this.ratio;
    if (resized) {
      this.width = width;
      this.height = height;
      this.ratio = ratio;
      this.renderer.setPixelRatio(ratio);
      this.composer.setSize(width, height);
      this.textureCanvas.width = Math.max(1, Math.floor(width * ratio));
      this.textureCanvas.height = Math.max(1, Math.floor(height * ratio));
    }

    const redrawn = resized || this.dirty || animatedSource;
    if (redrawn) {
      const ctx = this.ctx;
      ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
      ctx.clearRect(0, 0, width, height);
      this.draw(ctx, width, height);
      this.texture.needsUpdate = true;
      this.dirty = false;
    }

    const glitching = !!this.glitch && pulse.until > time;
    if (!redrawn && !glitching && !this.wasGlitching && !this.lines?.scrollSpeed) return;
    if (this.glitch)
      this.glitch.mode = glitching ? (pulse.strong ? GlitchMode.CONSTANT_WILD : GlitchMode.CONSTANT_MILD) : GlitchMode.DISABLED;
    if (this.chromatic) {
      const chromatic = this.config.chromatic;
      const intensity = (chromatic?.intensity ?? 100) / 100;
      const shift = glitching ? this.config.glitch.colorShift : 0;
      const angle = Math.floor(time / 40) * 2.39996;
      this.chromatic.blendMode.opacity.value = glitching ? 1 : intensity;
      const baseWeight = glitching ? intensity : 1;
      this.chromatic.offset.set(((chromatic?.offsetX ?? 0) * baseWeight + Math.cos(angle) * shift) / width,
        ((chromatic?.offsetY ?? 0) * baseWeight + Math.sin(angle) * shift) / width);
    }
    this.composer.render(delta);
    for (const source of this.sources) source.classList.add("obs-effects-source-hidden");
    this.wasGlitching = glitching;
  }

  dispose() {
    const canvas = this.renderer.domElement;
    canvas.removeEventListener("webglcontextlost", this.contextLost);
    for (const source of this.sources) source.classList.remove("obs-effects-source-hidden");
    canvas.remove();
    this.composer?.dispose();
    this.mesh.geometry.dispose();
    this.mesh.material.dispose();
    this.texture.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
  }
}
