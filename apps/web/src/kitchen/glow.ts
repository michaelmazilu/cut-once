import * as THREE from "three";
import type { Twin } from "@cutonce/schemas";

/**
 * The look of a recognised object: a soft blue light hugging its outline, like the headset's halo. It is the twin's own
 * shape grown by about a centimetre, brightest where its surface turns away from you (the silhouette) and clear where
 * it faces you, added on top of the room. Drawn after the room with the room's depth, so things in front hide it.
 */

const GROW = 0.012;

const halo = (color: THREE.ColorRepresentation, strength: number) => new THREE.ShaderMaterial({
  uniforms: { uColor: { value: new THREE.Color(color) }, uStrength: { value: strength } },
  vertexShader: `
    varying vec3 vNormal; varying vec3 vView;
    void main() {
      vec4 w = modelMatrix * vec4(position, 1.0);
      vNormal = normalize(mat3(modelMatrix) * normal);
      vView = normalize(cameraPosition - w.xyz);
      gl_Position = projectionMatrix * viewMatrix * w;
    }`,
  fragmentShader: `
    uniform vec3 uColor; uniform float uStrength;
    varying vec3 vNormal; varying vec3 vView;
    void main() {
      float edge = 1.0 - abs(dot(normalize(vNormal), normalize(vView)));
      float a = pow(edge, 2.2) * uStrength;
      gl_FragColor = vec4(uColor * a, a);
    }`,
  transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
});

function shapeGeometry(t: Twin, grow: number): THREE.BufferGeometry {
  const s = t.shape;
  if (s.type === "box") return new THREE.BoxGeometry(s.size[0] + 2 * grow, s.size[1] + 2 * grow, s.size[2] + 2 * grow, 1, 1, 1);
  const g = new THREE.CylinderGeometry(s.diameter / 2 + grow, s.diameter / 2 + grow, s.length + 2 * grow, 48, 1);
  if (s.axis === "x") g.rotateZ(-Math.PI / 2); else if (s.axis === "z") g.rotateX(Math.PI / 2);
  return g;
}

/** The glow for one twin, placed where the scan found it (its centre and turn, in the room's frame). */
export function twinGlow(t: Twin, opts: { named: boolean; highlighted: boolean }): THREE.Group {
  const g = new THREE.Group();
  g.name = `glow ${t.twin_id}`;
  g.position.set(...t.position);
  g.rotation.y = THREE.MathUtils.degToRad(t.yaw_deg);
  const color = opts.highlighted ? 0xffd84d : opts.named ? 0x4fb6ff : 0x7c8da3;
  const strength = opts.highlighted ? 2.2 : opts.named ? 1.5 : 0.8;
  g.add(new THREE.Mesh(shapeGeometry(t, GROW), halo(color, strength)));        // the halo just outside the outline
  g.add(new THREE.Mesh(shapeGeometry(t, 0.002), halo(color, strength * 0.7))); // the rim on the object itself
  return g;
}

export function disposeGroup(root: THREE.Object3D): void {
  root.traverse((o) => {
    const m = o as THREE.Mesh;
    if (m.isMesh) { m.geometry.dispose(); (Array.isArray(m.material) ? m.material : [m.material]).forEach((x) => x.dispose()); }
  });
}
