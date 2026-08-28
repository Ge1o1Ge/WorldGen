export const MIN_SPHERE_ZOOM = 0.68;
export const MAX_SPHERE_ZOOM = 64;

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const normalize = q => {
  const length = Math.hypot(...q);
  return q.map(value => value / length);
};
const multiply = (a, b) => normalize([
  a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
  a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
  a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
  a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]
]);

function rotationBetween(from, to) {
  const dot = from.x * to.x + from.y * to.y + from.z * to.z;
  if (dot < -0.999999) {
    const axis = Math.abs(from.x) < 0.8
      ? [0, from.z, -from.y, 0] : [-from.z, 0, from.x, 0];
    return normalize(axis);
  }
  return normalize([
    from.y * to.z - from.z * to.y,
    from.z * to.x - from.x * to.z,
    from.x * to.y - from.y * to.x,
    1 + dot
  ]);
}

function viewRay(x, y, geometry, clampToRim = false) {
  let sx = (x - geometry.centerX) / geometry.radius;
  let sy = -(y - geometry.centerY) / geometry.radius;
  const length = Math.hypot(sx, sy);
  if (length > 1) {
    if (!clampToRim) return null;
    sx /= length;
    sy /= length;
  }
  return { x: sx, y: sy, z: Math.sqrt(Math.max(0, 1 - sx * sx - sy * sy)) };
}

export class SphereCamera {
  constructor() { this.reset(); }

  setOrientation(q) {
    this.orientation = normalize(q);
    const [x, y, z, w] = this.orientation;
    this.matrix = [
      1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
      2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
      2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)
    ];
  }

  reset() {
    this.zoom = 1;
    this.setOrientation(multiply([0, Math.sin(-0.55 / 2), 0, Math.cos(-0.55 / 2)],
      [Math.sin(-0.28 / 2), 0, 0, Math.cos(-0.28 / 2)]));
  }

  geometry(width, height, zoom = this.zoom) {
    return { centerX: width * 0.5, centerY: height * 0.49, radius: Math.min(width, height) * 0.405 * zoom };
  }

  toWorld(x, y, z) {
    const m = this.matrix;
    return { x: m[0] * x + m[1] * y + m[2] * z, y: m[3] * x + m[4] * y + m[5] * z, z: m[6] * x + m[7] * y + m[8] * z };
  }

  toView(point) {
    const m = this.matrix;
    return { x: m[0] * point.x + m[3] * point.y + m[6] * point.z,
      y: m[1] * point.x + m[4] * point.y + m[7] * point.z,
      z: m[2] * point.x + m[5] * point.y + m[8] * point.z };
  }

  worldAt(x, y, width, height) {
    const ray = viewRay(x, y, this.geometry(width, height));
    return ray ? this.toWorld(ray.x, ray.y, ray.z) : null;
  }

  beginDrag(x, y, width, height) {
    return { orientation: [...this.orientation], ray: viewRay(x, y, this.geometry(width, height), true) };
  }

  drag(start, x, y, width, height) {
    const ray = viewRay(x, y, this.geometry(width, height), true);
    // The grabbed world point follows the pointer, in both axes and across poles.
    this.setOrientation(multiply(start.orientation, rotationBetween(ray, start.ray)));
  }

  zoomAt(value, x, y, width, height) {
    const next = clamp(value, MIN_SPHERE_ZOOM, MAX_SPHERE_ZOOM);
    const oldRay = viewRay(x, y, this.geometry(width, height));
    const newRay = viewRay(x, y, this.geometry(width, height, next));
    if (oldRay && newRay) this.setOrientation(multiply(this.orientation, rotationBetween(newRay, oldRay)));
    this.zoom = next;
  }

  focus(point, zoom = 16) {
    const yaw = Math.atan2(point.x, point.z);
    const pitch = -Math.asin(clamp(point.y, -1, 1));
    this.setOrientation(multiply([0, Math.sin(yaw / 2), 0, Math.cos(yaw / 2)],
      [Math.sin(pitch / 2), 0, 0, Math.cos(pitch / 2)]));
    this.zoom = clamp(zoom, MIN_SPHERE_ZOOM, MAX_SPHERE_ZOOM);
  }
}
