import * as THREE from "three";
import { partAabb, union, type Aabb } from "@cutonce/project-model";
import type { Plan } from "@cutonce/schemas";
import type { View } from "../../preview/previewParams";

/** The plan's bounds in model space (metres, +Y up). A 1 m cube at the origin when nothing has bounds. */
export function planBounds(plan: Plan): Aabb {
  const boxes = plan.parts.map(partAabb).filter((b): b is Aabb => b !== null);
  return union(boxes) ?? { min: [-0.5, 0, -0.5], max: [0.5, 1, 0.5] };
}

/**
 * Puts the camera where a person would stand. `operator`: at the near (+Z) edge, eyes at 1.6 m or 0.6 m
 * above the model, whichever is higher (true 1:1 scale, like the headset). `top`: straight down.
 * `orbit`: a three-quarter view that fits the whole model. Returns the point it looks at.
 */
export function placeCamera(camera: THREE.PerspectiveCamera, box: Aabb, view: View): THREE.Vector3 {
  const min = new THREE.Vector3(...box.min), max = new THREE.Vector3(...box.max);
  const centre = min.clone().add(max).multiplyScalar(0.5);
  const size = max.clone().sub(min);
  const radius = Math.max(size.length() / 2, 0.05);
  const halfFov = THREE.MathUtils.degToRad(camera.fov / 2);
  if (view === "operator") {
    camera.position.set(centre.x, Math.max(1.6, max.y + 0.6), max.z + Math.max(0.6, size.z * 0.5));
  } else if (view === "top") {
    // Far enough that tall parts (legs) don't loom at the lens: the layout reads like a plan drawing.
    const fitH = Math.max(size.z, size.x / Math.max(camera.aspect, 0.1)) / 2;
    camera.position.set(centre.x, max.y + Math.max((fitH / Math.tan(halfFov)) * 1.2, radius * 1.5), centre.z + 0.001);
  } else {
    const fitAspect = Math.min(1, camera.aspect);
    const dist = (radius / Math.sin(Math.atan(Math.tan(halfFov) * fitAspect))) * 1.1;
    camera.position.copy(centre).addScaledVector(new THREE.Vector3(0.75, 0.7, 1).normalize(), dist);
  }
  const dist = camera.position.distanceTo(centre);
  camera.near = Math.max(dist / 500, 0.01);
  camera.far = dist + radius * 20;
  camera.lookAt(centre);
  camera.updateProjectionMatrix();
  return centre;
}
