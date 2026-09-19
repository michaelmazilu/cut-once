import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";

const cache = new Map<string, Promise<THREE.Group>>();

/**
 * Loads a plan's GLB once and returns a copy of the node named `node` (node name = part id, by contract).
 * A GLB whose coordinates are baked into the vertices needs no parent transform.
 */
export function loadMeshNode(url: string, node: string, headers: Record<string, string>): Promise<THREE.Object3D | null> {
  let scene = cache.get(url);
  if (!scene) {
    const loader = new GLTFLoader();
    loader.setRequestHeader(headers);
    scene = loader.loadAsync(url).then((g) => g.scene);
    scene.catch(() => cache.delete(url)); // let a later mount retry
    cache.set(url, scene);
  }
  return scene.then((s) => s.getObjectByName(node)?.clone(true) ?? null);
}
