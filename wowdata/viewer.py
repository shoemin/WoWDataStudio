"""Quick-look viewer: a single HTML page with the model embedded (GLB as base64) and three.js from a pinned CDN.

The page works when opened straight from disk (file://): nothing is fetched except three.js itself
(three@0.160.0 from cdn.jsdelivr.net, MIT), so it needs an internet connection the first time.
Blender stays the main viewer; this page is for a fast look without opening Blender.
"""
import html
import json

THREE_VERSION = '0.160.0'

TEMPLATE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
/* layout: full-window canvas, one floating control panel top-left, status line bottom-left */
:root {
  --bg: #e9ebef; --panel: rgba(255,255,255,.92); --fg: #1d2129; --muted: #5d6573; --line: #c9ced8; --accent: #2f6fb3;
  --ui: system-ui, "Segoe UI", sans-serif; --mono: ui-monospace, Consolas, monospace;
}
@media (prefers-color-scheme: dark) {
  :root { --bg: #15181e; --panel: rgba(28,32,40,.92); --fg: #e4e7ec; --muted: #9aa3b2; --line: #343b48; --accent: #6aa6e8; color-scheme: dark; }
}
html, body { height: 100%; margin: 0; background: var(--bg); color: var(--fg); font: 13px/1.4 var(--ui); overflow: hidden; }
canvas { display: block; }
#panel { position: absolute; top: 12px; left: 12px; width: min(300px, calc(100vw - 24px)); background: var(--panel);
  border: 1px solid var(--line); border-radius: 6px; padding: 10px 12px; display: grid; gap: 8px; box-sizing: border-box; }
#panel h1 { font-size: 14px; margin: 0; font-weight: 600; overflow-wrap: anywhere; }
#panel .sub { color: var(--muted); font-family: var(--mono); font-size: 11px; overflow-wrap: anywhere; }
label { display: grid; gap: 3px; color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
select, button { font: inherit; color: var(--fg); background: var(--bg); border: 1px solid var(--line); border-radius: 4px; padding: 4px 6px; }
select:focus-visible, button:focus-visible, input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.row { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; }
.row label { display: flex; gap: 4px; align-items: center; text-transform: none; letter-spacing: 0; font-size: 12px; color: var(--fg); }
#geosets { max-height: 30vh; overflow: auto; display: grid; gap: 2px; font-family: var(--mono); font-size: 11px; }
#geosets label { display: flex; gap: 4px; align-items: center; text-transform: none; letter-spacing: 0; color: var(--fg); font-size: 11px; }
details summary { cursor: pointer; color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
#status { position: absolute; left: 12px; bottom: 10px; color: var(--muted); font-family: var(--mono); font-size: 11px; }
</style>
<script type="importmap">
{ "imports": {
  "three": "https://cdn.jsdelivr.net/npm/three@__THREE__/build/three.module.js",
  "three/addons/": "https://cdn.jsdelivr.net/npm/three@__THREE__/examples/jsm/"
} }
</script>
</head>
<body>
<div id="panel">
  <div><h1>__TITLE__</h1><div class="sub">__SUBTITLE__</div></div>
  <label for="anim">Animation <select id="anim"></select></label>
  <div class="row">
    <button id="play" type="button">Pause</button>
    <label for="speed">Speed <select id="speed"><option>0.25</option><option>0.5</option><option selected>1</option><option>2</option></select></label>
  </div>
  <div class="row">
    <label for="skel"><input id="skel" type="checkbox"> Skeleton</label>
    <label for="wire"><input id="wire" type="checkbox"> Wireframe</label>
    <label for="grid"><input id="grid" type="checkbox" checked> Grid</label>
  </div>
  <details><summary>Geosets</summary><div id="geosets"></div></details>
</div>
<div id="status">loading three.js ...</div>
<script id="model" type="application/octet-stream">__GLB__</script>
<script type="module">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const status = document.getElementById('status');
const dark = matchMedia('(prefers-color-scheme: dark)').matches;
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setPixelRatio(devicePixelRatio);
renderer.setSize(innerWidth, innerHeight);
renderer.outputColorSpace = THREE.SRGBColorSpace;
document.body.prepend(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(dark ? 0x15181e : 0xe9ebef);
scene.add(new THREE.HemisphereLight(0xffffff, 0x445566, 1.6));
const sun = new THREE.DirectionalLight(0xffffff, 1.6); sun.position.set(3, 6, 4); scene.add(sun);
const camera = new THREE.PerspectiveCamera(40, innerWidth / innerHeight, 0.01, 5000);
const controls = new OrbitControls(camera, renderer.domElement);
const grid = new THREE.GridHelper(10, 10, 0x888888, dark ? 0x2a2f38 : 0xc8ccd4); scene.add(grid);
const clock = new THREE.Clock();
let mixer = null, action = null, helper = null, playing = true;

const b64 = document.getElementById('model').textContent.trim();
const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
new GLTFLoader().parse(bytes.buffer, '', gltf => {
  const root = gltf.scene;
  scene.add(root);
  const geoBox = document.getElementById('geosets');
  root.traverse(o => {
    const ex = o.userData || {};
    if (o.isMesh || o.isSkinnedMesh) {
      o.frustumCulled = false;
      const node = o.parent && o.parent.userData && 'wow_default_visible' in o.parent.userData ? o.parent : o;
      const vis = node.userData.wow_default_visible !== false;
      node.visible = vis;
      if (node.userData.wow_geoset_id !== undefined || node.userData.wow_collision) {
        const id = 'g' + node.id;
        const lab = document.createElement('label');
        lab.htmlFor = id;
        lab.innerHTML = '<input type="checkbox" id="' + id + '"> ' + node.name;
        lab.firstChild.checked = vis;
        lab.firstChild.onchange = e => { node.visible = e.target.checked; };
        geoBox.append(lab);
      }
    }
  });
  // bounds of the bind-pose vertices (skinned bounds are not valid before the first frame)
  root.updateMatrixWorld(true);
  const box = new THREE.Box3();
  root.traverse(o => {
    if (o.isMesh && o.visible && (!o.parent || o.parent.visible)) {
      const b = new THREE.Box3().setFromBufferAttribute(o.geometry.attributes.position);
      box.union(b.applyMatrix4(o.matrixWorld));
    }
  });
  if (box.isEmpty()) box.setFromObject(root);
  const size = box.getSize(new THREE.Vector3()).length() || 1, c = box.getCenter(new THREE.Vector3());
  grid.scale.setScalar(size / 5); grid.position.y = box.min.y;
  controls.target.copy(c);
  // M2 models face +X (glTF x = M2 x): look at the front, slightly from the side and above
  camera.position.copy(c).add(new THREE.Vector3(size * 1.5, size * 0.3, size * 0.55));
  camera.near = size / 500; camera.far = size * 50; camera.updateProjectionMatrix();
  helper = new THREE.SkeletonHelper(root); helper.visible = false; scene.add(helper);
  mixer = new THREE.AnimationMixer(root);
  const sel = document.getElementById('anim');
  const none = document.createElement('option'); none.value = -1; none.textContent = '(bind pose)'; sel.append(none);
  gltf.animations.forEach((a, i) => {
    const o = document.createElement('option'); o.value = i;
    o.textContent = a.name + '  ' + a.duration.toFixed(2) + ' s'; sel.append(o);
  });
  const play = i => {
    if (action) action.stop();
    action = null;
    if (i >= 0) { action = mixer.clipAction(gltf.animations[i]); action.play(); }
  };
  const stand = gltf.animations.findIndex(a => a.name === 'Stand');
  sel.value = stand >= 0 ? stand : (gltf.animations.length ? 0 : -1);
  play(+sel.value);
  sel.onchange = () => play(+sel.value);
  status.textContent = gltf.animations.length + ' animations. Drag to orbit, right-drag to pan, wheel to zoom.';
}, err => { status.textContent = 'could not load the model: ' + err; });

document.getElementById('play').onclick = e => { playing = !playing; e.target.textContent = playing ? 'Pause' : 'Play'; };
document.getElementById('speed').onchange = e => { if (mixer) mixer.timeScale = +e.target.value; };
document.getElementById('skel').onchange = e => { if (helper) helper.visible = e.target.checked; };
document.getElementById('grid').onchange = e => { grid.visible = e.target.checked; };
document.getElementById('wire').onchange = e => scene.traverse(o => { if (o.isMesh) [].concat(o.material).forEach(m => m.wireframe = e.target.checked); });
addEventListener('resize', () => { camera.aspect = innerWidth / innerHeight; camera.updateProjectionMatrix(); renderer.setSize(innerWidth, innerHeight); });
renderer.setAnimationLoop(() => {
  const dt = clock.getDelta();
  if (mixer && playing) mixer.update(dt);
  controls.update();
  renderer.render(scene, camera);
});
</script>
</body>
</html>
"""


def write_html(path, glb_b64, title, subtitle=''):
    page = (TEMPLATE.replace('__THREE__', THREE_VERSION)
            .replace('__TITLE__', html.escape(title))
            .replace('__SUBTITLE__', html.escape(subtitle))
            .replace('__GLB__', glb_b64))
    with open(path, 'w', encoding='utf-8') as f:
        f.write(page)
    return path


def info_json(rep):
    return json.dumps({k: rep[k] for k in ('name', 'bones', 'sequences', 'animations', 'vertices')})
