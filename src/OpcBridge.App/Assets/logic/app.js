'use strict';

// Plant logic authoring page, served by the bridge at /logic.
//
// It reads and writes the same store the HMI's Logic page and the phone use:
//   GET  /api/logic              blocks + area tag lists + version
//   POST /api/logic/blocks       { block } -> saved block (or { error })
//   DELETE /api/logic/blocks/{id}
//   GET  /api/logic/state        live evaluated state (polled; the HMI hub pushes the same DTO)
//
// The element vocabulary below is a contract with the bridge
// (LogicElementKinds / LogicConditionOps / LogicElementKinds.InputRange in
// OpcBridge.Client) and LogicPageTests pins it against the C# lists.
//
// The three tabs split the work the way the HMI's Logic page splits it: Editor authors the block
// and its rows, each row picking one of the area's tags by name (or the internal relay it reads);
// Tags owns every assignment — the block's area and the area's list of named tags, each bound to
// its source and tag — so a signal is assigned once and only there; Result is the read-only
// operator view of the bridge's own evaluation.

const KINDS = ['contact', 'coil', 'and', 'or', 'xor', 'not', 'ton', 'tof', 'tp', 'ctu', 'ctd', 'sr', 'rs', 'r_trig', 'f_trig'];
const OPS = ['on', 'off', 'gt', 'lt', 'eq'];
const BLOCK_KINDS = ['interlock', 'permissive', 'sequence'];
const SEVERITIES = ['block', 'warn'];
const INPUT_MAX = { contact: 0, coil: 1, and: 32, or: 32, xor: 32, not: 1, ton: 1, tof: 1, tp: 1, ctu: 2, ctd: 2, sr: 2, rs: 2, r_trig: 1, f_trig: 1 };
const LIMITS = { depth: 16, elements: 256 };

const KIND_LABEL = {
  contact: 'Contact', coil: 'Coil (relay)',
  and: 'AND gate', or: 'OR gate', xor: 'XOR gate', not: 'NOT gate',
  ton: 'TON on-delay', tof: 'TOF off-delay', tp: 'TP pulse',
  ctu: 'CTU up counter', ctd: 'CTD down counter',
  sr: 'SR latch', rs: 'RS latch', r_trig: 'R_TRIG rising', f_trig: 'F_TRIG falling'
};
const OP_LABEL = { on: 'on (NO)', off: 'off (NC)', gt: '>', lt: '<', eq: '=' };
const SEV_LABEL = { block: 'blocks', warn: 'warn only' };

const POLL_MS = 1000;
const RELAY = '__relay__';
const NEW_TAG = '__new__';

const api = {
  defs: '/api/logic',
  save: '/api/logic/blocks',
  del: (id) => '/api/logic/blocks/' + encodeURIComponent(id),
  state: '/api/logic/state'
};

let defs = { blocks: [], tagSources: [], version: 0 };
let version = 0;
let draft = null;          // deep copy of the block being edited
let draftForm = 'network'; // 'network' when editable, 'conditions' when the simple form authored it
let dirty = false;
let blockStates = new Map();
let elementStates = new Map();
let areas = [];            // area tag lists, by name
let lastPollOk = 0;
let tab = 'editor';   // editor | tags | result
let catalog = null;   // the bridge's mapped tags (GET /api/hmi/tags), for the Tags tab
let catalogSources = []; // those tags, grouped by source
let tagArea = '';     // the area whose tag list the Tags tab is editing — the block's own area
let tagDraft = null;  // that list, edited locally until Save
let tagDrafts = new Map(); // a list's unsaved edits by area name, so switching areas never drops them
let newTagForId = ''; // the editor row whose TAG picker is naming a new tag
let lastEvaluated = null; // the snapshot's EvaluatedUtc, for the Result tab's freshness
let catalogFetchedAt = 0; // when the bridge's tag snapshot was last read
let feedState = 'unknown'; // live | reconnecting | failed — the Result header's freshness follows it

const el = (id) => document.getElementById(id);

// ---------------------------------------------------------------- helpers

function h(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === undefined || child === null || child === false) continue;
    node.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
  }
  return node;
}

const uuid = () => (crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
  const r = (Math.random() * 16) | 0;
  return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
}));

const clone = (value) => JSON.parse(JSON.stringify(value));

function isContact(e) { return e.kind === 'contact'; }
function isCoil(e) { return e.kind === 'coil'; }
function isTimer(e) { return e.kind === 'ton' || e.kind === 'tof' || e.kind === 'tp'; }
function isCounter(e) { return e.kind === 'ctu' || e.kind === 'ctd'; }
function maxInputs(e) { return INPUT_MAX[e.kind] || 0; }
function readsRelay(e) { return isContact(e) && (e.relay || '') !== ''; }

function areaTags(areaName) {
  const wanted = (areaName || '').trim().toLowerCase();
  return areas.find((a) => (a.name || '').trim().toLowerCase() === wanted) || null;
}

// The area a block reads: its area, else the stored tag source, else its group.
function blockArea(block) {
  if (block.area && block.area.trim()) return block.area.trim();
  if (block.tagSource && block.tagSource.trim()) return block.tagSource.trim();
  return (block.group || '').trim();
}

function countElements(list) {
  let n = 0;
  for (const e of list || []) n += 1 + countElements(e.inputs);
  return n;
}

// Which authoring form a block uses. Only the network form is edited here: the flat conditions
// and the sequence steps are authored in the HMI's Logic page, and this page shows the bridge's
// own evaluation of them read-only (the Result tab shows the same for every form).
function formOf(block) {
  if ((block.elements || []).length > 0) return 'network';
  if ((block.steps || []).length > 0) return 'steps';
  if ((block.conditions || []).length > 0) return 'conditions';
  return 'network';
}

// Paths are index chains: [0, 2, 1] is elements[0].inputs[2].inputs[1].
function parentArray(path) {
  let list = draft.elements;
  for (let i = 0; i < path.length - 1; i++) list = list[path[i]].inputs;
  return list;
}
function elementAt(path) { const list = parentArray(path); return list[path[path.length - 1]]; }

function newElement() {
  // The store refuses a contact without an operator sentence ("A contact needs its operator
  // sentence."), so a new row starts with a placeholder the author overwrites. The tag is the
  // author's to choose, and the store says so plainly if it is missing.
  return { id: uuid(), kind: 'contact', text: 'New condition', sourceId: '', itemId: '', tag: '', op: 'on', value: null, relay: '', ptMs: 0, pv: 0, inputs: [], severity: 'block', nextStepText: null };
}

function newBlock(name, kind, area, group) {
  return {
    id: uuid(), name: name, group: group || '', area: area || '', tagSource: '', description: '',
    kind: kind || 'interlock', enabled: true, order: defs.blocks.length,
    tags: [], elements: [newElement()], conditions: [], steps: [], actions: []
  };
}

// ---------------------------------------------------------------- network

function fetchJson(url, init) {
  return fetch(url, init).then(async (response) => {
    let body = null;
    try { body = await response.json(); } catch (e) { /* empty body */ }
    if (response.status === 401 || response.status === 403) {
      throw new Error((body && body.error) || 'Sign in at the dashboard first, then reload this page.');
    }
    if (!response.ok) throw new Error((body && body.error) || ('request failed (' + response.status + ')'));
    return body;
  });
}

function toast(message, kind) {
  const node = el('toast');
  node.textContent = message;
  node.className = 'toast' + (kind ? ' ' + kind : '');
  node.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { node.hidden = true; }, kind === 'err' ? 6000 : 2500);
}

function markDirty() {
  dirty = true;
  renderSaveBar();
}

function renderSaveBar() {
  const bar = el('savebar');
  if (!bar) return;
  const save = bar.querySelector('#btnSave');
  save.disabled = !dirty;
  const revert = bar.querySelector('#btnRevert');
  revert.disabled = !dirty;
  const msg = bar.querySelector('.msg');
  if (!msg.dataset.keep) {
    msg.className = 'msg';
    msg.textContent = dirty ? 'Unsaved changes.' : 'Saved state.';
  }
}

function reloadDefs(keepSelection) {
  return fetchJson(api.defs).then((body) => {
    defs = { blocks: body.blocks || [], tagSources: body.tagSources || [], version: body.version || 0 };
    version = defs.version;
    areas = defs.tagSources || [];
    renderRail();
    if (keepSelection && draft && defs.blocks.some((b) => b.id === draft.id)) {
      const fresh = defs.blocks.find((b) => b.id === draft.id);
      draft = clone(fresh);
      draftForm = formOf(fresh);
      dirty = false;
      renderEditor();
    }
    return defs;
  });
}

// ---------------------------------------------------------------- rendering

function renderRail() {
  const list = el('blockList');
  list.textContent = '';
  if (!defs.blocks.length) {
    list.appendChild(h('p', { class: 'muted small', text: 'No blocks yet.' }));
    return;
  }

  const byArea = new Map();
  for (const block of defs.blocks) {
    const area = (block.area || block.tagSource || '').trim() || '—';
    if (!byArea.has(area)) byArea.set(area, []);
    byArea.get(area).push(block);
  }

  for (const [area, blocks] of byArea) {
    list.appendChild(h('div', { class: 'group-head', text: area }));
    for (const block of blocks) {
      const state = blockStates.get(block.id);
      const row = h('button', {
        class: 'block-row' + (draft && draft.id === block.id ? ' on' : ''),
        type: 'button',
        'data-block': block.id,
        onclick: () => selectBlock(block.id)
      },
        h('span', { class: 'state-dot ' + (state ? state.state : ''), 'aria-hidden': 'true' }),
        h('span', { class: 'n', text: block.name || '(unnamed)' }),
        h('span', { class: 'kind', text: blockKindLabel(block.kind) + ((block.group || '').trim() ? ' · ' + block.group.trim() : '') }),
        h('span', { class: 'block-why', text: blockWhy(block, state) })
      );
      list.appendChild(row);
    }
  }
}

// The block row's second line, the way the retired app's result list read it: the state word and
// why it is blocked (or that everything is met), with a sequence's progress.
function blockWhy(block, state) {
  const why = !state ? 'reading…' : state.state === 'ready' ? 'all conditions met' : (state.reason || '');
  const progress = sequenceProgress(block, state);
  return [blockStateWord(state && state.state), why, progress].filter((part) => part).join(' · ');
}

// How the plant is doing overall: the retired app's one-line summary over every block.
function blockSummary() {
  if (!defs.blocks.length) return 'no logic blocks configured';
  let ready = 0;
  let blocked = 0;
  let noData = 0;
  for (const block of defs.blocks) {
    const word = (blockStates.get(block.id) || {}).state || 'unknown';
    if (word === 'ready') ready += 1;
    else if (word === 'blocked') blocked += 1;
    else if (word !== 'disabled') noData += 1;
  }

  return ready + ' ready · ' + blocked + ' blocked' + (noData ? ' · ' + noData + ' no data' : '');
}

function renderResult() {
  const box = el('result');
  box.textContent = '';
  if (!draft) { box.appendChild(h('p', { class: 'muted', text: 'No block selected.' })); return; }
  const state = blockStates.get(draft.id);
  const word = state ? state.state : 'unknown';

  box.appendChild(h('div', { class: 'verdict' },
    h('span', { class: 'stamp ' + word, text: blockStateWord(word) }),
    h('span', { class: 'muted small', text: draft.enabled ? '' : 'disabled block' })
  ));
  if (state && state.reason) box.appendChild(h('p', { class: 'reason', text: state.reason }));
  if (state && (state.steps || []).length) {
    for (const step of state.steps) {
      box.appendChild(h('p', { class: 'sub mono small', text: step.state + ' · ' + (step.reason || '') }));
    }
  }
  box.appendChild(h('p', { class: 'sub small mono', text: 'definitions v' + version + ' · ' + defs.blocks.length + ' blocks' }));
  box.appendChild(h('p', { class: 'sub small', text: blockSummary() }));
}

function renderEditor() {
  const editor = el('editor');
  editor.textContent = '';
  if (!draft) {
    editor.appendChild(h('div', { class: 'placeholder' },
      h('p', { text: 'Pick a block on the left to edit its network, or press + New for a new one.' }),
      h('p', { class: 'muted small', text: 'Live state updates once a second while this page is open.' })
    ));
    return;
  }

  editor.appendChild(renderTabs());

  if (tab === 'result') {
    renderResultTab(editor);
    return;
  }

  if (tab === 'tags') {
    renderTagsTab(editor);
    return;
  }

  editor.appendChild(renderHead());
  editor.appendChild(renderPin());

  if (draftForm !== 'network') {
    editor.appendChild(h('p', { class: 'readonly-note' },
      h('b', { text: draftForm === 'steps' ? 'This is a sequence block. ' : 'This block is authored in the flat conditions form. ' }),
      draftForm === 'steps'
        ? 'Its steps are edited in the HMI\'s Logic page; the Result tab here shows their live state.'
        : 'Its network below is the bridge\'s own expansion, shown read-only — the Result tab shows the same live; edit the conditions in the HMI\'s Logic page.'));
    editor.appendChild(h('div', { class: 'section-title', text: 'Network (IEC 61131-3) — read-only' }));
    const net = h('div', { class: 'net' });
    const state = blockStates.get(draft.id);
    const list = state ? state.elements : [];
    if (!list.length) net.appendChild(h('p', { class: 'muted small', text: 'The bridge has not evaluated this block yet.' }));
    for (const item of list) {
      net.appendChild(h('div', { class: 'el readonly d' + Math.min(item.depth || 0, 3), 'data-id': item.id, 'data-live': '1', style: '--depth:' + item.depth },
        renderLive(item.kind, '', item)
      ));
    }
    editor.appendChild(net);
    return;
  }

  editor.appendChild(h('div', { class: 'section-title', text: 'Network (IEC 61131-3) · ' + countElements(draft.elements) + ' / ' + LIMITS.elements + ' elements' }));
  editor.appendChild(renderNet());
  editor.appendChild(renderSaveBarNode());
}

function renderHead() {
  const head = h('div', { class: 'ed-head' });
  head.appendChild(h('div', { class: 'field' },
    h('label', { for: 'fName', text: 'Name' }),
    h('input', {
      type: 'text', id: 'fName', value: draft.name || '', maxlength: 64,
      oninput: (e) => { draft.name = e.target.value; markDirty(); }
    }),
    h('label', { for: 'fEnabled', text: 'Enabled' }),
    h('input', {
      type: 'checkbox', id: 'fEnabled', checked: draft.enabled !== false,
      onchange: (e) => { draft.enabled = e.target.checked; markDirty(); }
    })
  ));
  head.appendChild(h('div', { class: 'field' },
    h('label', { for: 'fKind', text: 'Kind' }),
    h('select', {
      id: 'fKind',
      onchange: (e) => { draft.kind = e.target.value; markDirty(); }
    }, BLOCK_KINDS.map((k) => h('option', { value: k, text: k, selected: draft.kind === k }))),
    h('label', { for: 'fGroup', text: 'Group' }),
    h('input', {
      type: 'text', id: 'fGroup', list: 'groupNames', value: draft.group || '', maxlength: 40,
      oninput: (e) => { draft.group = e.target.value; markDirty(); }
    })
  ));
  head.appendChild(h('div', { class: 'field' },
    h('label', { for: 'fDesc', text: 'Note' }),
    h('input', {
      type: 'text', id: 'fDesc', value: draft.description || '',
      oninput: (e) => { draft.description = e.target.value; markDirty(); }
    })
  ));
  head.appendChild(h('div', { class: 'field' },
    h('label', { for: 'fTags', text: 'Labels' }),
    h('input', {
      type: 'text', id: 'fTags', value: (draft.tags || []).join(', '), placeholder: 'comma separated, up to 8',
      oninput: (e) => {
        draft.tags = e.target.value.split(',').map((t) => t.trim()).filter((t) => t).slice(0, 8);
        markDirty();
      }
    })
  ));

  const groups = Array.from(new Set(defs.blocks.map((b) => (b.group || '').trim()).filter((g) => g)));
  const datalistGroup = h('datalist', { id: 'groupNames' }, groups.map((g) => h('option', { value: g })));
  head.appendChild(datalistGroup);

  // The block's AREA and its tags are assigned on the Tags tab; this is the read-only answer to
  // "which tags does this block resolve through" while its rows are authored.
  const stateBox = h('div', { class: 'muted small', id: 'areaState' });
  head.appendChild(stateBox);
  setTimeout(() => { renderAreaState(); syncNetPinning(); }, 0);
  return head;
}

// What tags the block's sentences resolve through: the area's list, and whether the area exists.
function renderAreaState() {
  const box = el('areaState');
  if (!box || !draft) return;
  box.textContent = '';
  const name = blockArea(draft);
  const list = areaTags(name);
  if (!name) {
    box.appendChild(h('span', { class: 'warnline', text: 'No area — named tags cannot resolve. Set this block\'s area on the Tags tab.' }));
    return;
  }
  if (!list) {
    box.appendChild(h('span', { class: 'warnline', text: 'The bridge has no tags for area "' + name + '". Assign them on the Tags tab.' }));
    return;
  }
  const bound = (list.tags || []).filter((t) => (t.sourceId || '').trim() && (t.itemId || '').trim()).length;
  box.appendChild(h('span', { text: 'Area ' + name + ': ' + list.tags.length + ' tags (' + bound + ' assigned) · the area and its tags are on the Tags tab' }));
}

// The box the row kept at the top is written into: empty and taking no room in the flow, because
// the strip rides over the rows rather than pushing them down (see .pin-wrap). syncNetPinning fills
// it and shows it, and hides it again when the row at the top of the editor has no parent.
function renderPin() {
  return h('div', { class: 'pin-wrap', id: 'pinWrap', hidden: true });
}

function renderNet() {
  const net = h('div', { class: 'net', id: 'net' });
  (draft.elements || []).forEach((element, index) => net.appendChild(renderNode(element, [index])));
  const count = countElements(draft.elements);
  net.appendChild(h('div', { class: 'el-edit', style: 'margin-top:4px' },
    h('button', {
      class: 'btn small', type: 'button', disabled: count >= LIMITS.elements,
      onclick: () => { draft.elements.push(newElement()); markDirty(); renderEditor(); }
    }, '+ root element'),
    h('span', { class: 'muted small', text: 'Roots are ANDed: every root must hold for the block to be ready.' })
  ));
  syncNetPinning();
  return net;
}

// One element and the inputs under it, written as nested nodes rather than as one flat list of
// indented rows. A row rides the top of the editor while its own children are still on screen —
// and a sticky box is only released at the end of its containing block, which here is the row
// plus its children. Written flat, every parent would stay pinned to the end of the network
// instead of to the end of its own children, and the leaf rows would carry the same offset for
// nothing.
function renderNode(element, path) {
  const node = h('div', { class: 'el-node' });
  node.appendChild(renderElement(element, path));
  if (!element.inputs.length) return node;
  const kids = h('div', { class: 'el-kids' });
  for (let i = 0; i < element.inputs.length; i++) kids.appendChild(renderNode(element.inputs[i], path.concat(i)));
  node.appendChild(kids);
  return node;
}

// What the rows ride under: the head of whichever tab is open — the editor's properties, the
// result's own verdict — because it rides the same scroll, so what they have to clear is its
// height. That depends on the window (the note under the fields wraps) and on the text the poll
// writes into the result head, so it is measured after every render rather than assumed. The
// editor's top pad is read back from the stylesheet: a sticky offset measures from the editor's
// content box, and the head's box top sits exactly that far above it.
//
// The strip on that line is the parent of the rows below it: both tabs write their rows nested
// (.el-node / .rl-node), so the row that owns what is on screen rides above it, and it leaves when
// the last of its children has gone. Which row that is changes with the scroll and cannot be said
// in the stylesheet, so the script reads it here. The row the head is cutting through is taken
// first, when it has children of its own under it: it is the parent of everything below from the
// moment it meets the head, and the strip is what keeps its name and its reading while the head
// takes its row. Only then does the question fall to the row above the first one the head has not
// already taken — and a row with nothing under it is never shown, as it parents nothing that is on
// screen. The strip carries the row's own reading rather than a copy of its controls: what the
// parent is and what it says, while the row itself is off the screen, and nowhere a second place
// to edit it.
function syncNetPinning() {
  const editor = el('editor');
  const head = editor ? editor.querySelector('.ed-head, .res-head') : null;
  if (!editor || !head) return;
  const padTop = parseFloat(getComputedStyle(editor).getPropertyValue('--pad-top')) || 0;
  // On the editor itself: the strip rides that line while the rows only inherit it.
  editor.style.setProperty('--net-stick-top', Math.round(head.offsetHeight - padTop) + 'px');

  const strip = el('pinWrap');
  if (!strip) return;
  const line = head.getBoundingClientRect().bottom;
  let first = null;
  let cutByHead = false;
  for (const row of editor.querySelectorAll('.el-node > .el, .rl-node > .rl')) {
    const rect = row.getBoundingClientRect();
    if (rect.bottom > line + 1) { first = row; cutByHead = rect.top < line; break; }
  }

  const node = first ? first.parentElement : null;
  let parentRow = null;
  if (node) {
    // A node holds its row first and the children under it second, so one child means none.
    const ownChildren = node.children.length > 1;
    if (cutByHead && ownChildren) {
      parentRow = first;
    } else if (node.parentElement && (node.parentElement.classList.contains('el-kids') || node.parentElement.classList.contains('rl-kids'))) {
      parentRow = node.parentElement.previousElementSibling;
    }
  }

  const id = parentRow ? parentRow.dataset.id : '';
  if (strip.dataset.parent === id) return;

  strip.dataset.parent = id;
  strip.textContent = '';
  strip.hidden = !parentRow;
  if (!parentRow) return;
  strip.appendChild(pinCopy(parentRow));
}

// The row kept at the top, as the strip shows it: the editor row's own live line, or the head of a
// result card. Both are what the row reads, in the row's own ground and indent, and both carry the
// row's id and its live hook so the poll repaints the copy exactly as it repaints the row.
function pinCopy(row) {
  if (row.classList.contains('rl')) {
    const card = row.querySelector('.rl-card');
    const head = card ? card.querySelector('.rl-head') : null;
    const copy = h('div', { class: card ? card.className : 'rl-card' }, head ? head.cloneNode(true) : null);
    // A card is repainted from the definition it carries; a copy without it would drift.
    if (card) copy.__def = card.__def;
    return h('div', {
      class: 'rl pin-strip', 'data-id': row.dataset.id, 'data-live': '1',
      style: '--depth:' + (row.style.getPropertyValue('--depth') || '0')
    }, copy);
  }

  const live = row.querySelector('.el-live');
  const level = Math.min(parseInt(row.style.getPropertyValue('--depth'), 10) || 0, 3);
  return h('div', {
    class: 'el pin-strip d' + level, 'data-id': row.dataset.id, 'data-live': '1',
    style: '--depth:' + (row.style.getPropertyValue('--depth') || '0')
  }, live ? live.cloneNode(true) : null);
}

// The strip follows the scroll, not every event's worth of it: one measurement per frame is all it
// needs, and the measurement itself forces the layout.
let pinFrame = 0;
function schedulePinnedRows() {
  if (pinFrame) return;
  pinFrame = requestAnimationFrame(() => { pinFrame = 0; syncNetPinning(); });
}

function renderElement(element, path) {
  const depth = path.length - 1;
  const state = elementStates.get(element.id);
  const cls = 'el d' + Math.min(depth, 3);
  const row = h('div', { class: cls, 'data-id': element.id, 'data-live': '1', style: '--depth:' + depth });
  row.appendChild(renderLive(element.kind, element.text, state));
  row.appendChild(renderControls(element, path));
  return row;
}

// The editor's live chip, the HMI's own rule: a contact reads 1 / 0 ("no data" while unknown), a
// numeric condition keeps its live value — a level above 50 % is not a bit.
function liveChip(state) {
  if (!state) return '—';
  if (state.kind !== 'contact') return stateWord(state.state);
  const should = state.should || '';
  const numeric = should.startsWith('>') || should.startsWith('<') || should.startsWith('=');
  if (numeric) return state.valueText && state.valueText !== '—' ? state.valueText : 'no data';
  return state.state === 'true' ? '1' : state.state === 'false' ? '0' : 'no data';
}

function renderLive(kind, text, state) {
  const label = KIND_LABEL[kind] || kind;
  const box = h('div', { class: 'el-live' });
  box.appendChild(h('div', { class: 'lead' },
    h('span', { class: 'q unknown', text: '—' }),
    h('span', { class: 'el-kind', text: label }),
    text ? h('span', { class: 'el-note', text: text }) : null));
  box.appendChild(h('span', { class: 'reading', text: '' }));
  if (state) paintLive(box, kind, state);
  return box;
}

function paintLive(row, kind, state) {
  const q = row.querySelector('.q');
  const reading = row.querySelector('.reading');
  const verdict = state.state;
  q.className = 'q ' + (verdict === 'true' ? 'on' : verdict === 'false' ? 'off' : 'unknown');
  q.textContent = liveChip(state);

  const bits = [];
  if (kind === 'contact') {
    const comparison = shouldLine(state, null);
    if (comparison) bits.push(comparison);
    if (state.valueText && state.valueText !== '—') bits.push(state.valueText);
  } else {
    const progress = progressLine(state, null);
    if (progress) bits.push(progress);
  }
  reading.textContent = bits.join(' · ');
  if (kind === 'contact' && state.matches === false && verdict !== 'unknown') reading.className = 'reading match-no';
  else if (kind === 'contact' && state.matches === true) reading.className = 'reading match-ok';
  else reading.className = 'reading';
}

function renderControls(element, path) {
  const tools = h('div', { class: 'tools' });
  const index = path[path.length - 1];
  const siblings = parentArray(path);

  const rerender = () => { markDirty(); renderEditor(); };

  tools.appendChild(h('button', {
    class: 'btn small icon', type: 'button', title: 'Add a sibling right after', 'aria-label': 'Add sibling after',
    disabled: countElements(draft.elements) >= LIMITS.elements,
    onclick: () => { siblings.splice(index + 1, 0, newElement()); rerender(); }
  }, '+ after'));
  tools.appendChild(h('button', {
    class: 'btn small icon', type: 'button', title: 'Add an input inside this element', 'aria-label': 'Add input',
    disabled: maxInputs(element) === 0 || element.inputs.length >= maxInputs(element) || countElements(draft.elements) >= LIMITS.elements,
    onclick: () => { element.inputs.push(newElement()); rerender(); }
  }, '+ in'));
  tools.appendChild(h('button', {
    class: 'btn small icon', type: 'button', title: 'Move up', 'aria-label': 'Move up', disabled: index === 0,
    onclick: () => { siblings.splice(index - 1, 0, siblings.splice(index, 1)[0]); rerender(); }
  }, '↑'));
  tools.appendChild(h('button', {
    class: 'btn small icon', type: 'button', title: 'Move down', 'aria-label': 'Move down', disabled: index === siblings.length - 1,
    onclick: () => { siblings.splice(index + 1, 0, siblings.splice(index, 1)[0]); rerender(); }
  }, '↓'));
  const nestable = index > 0 && maxInputs(siblings[index - 1]) > 0
    && siblings[index - 1].inputs.length < maxInputs(siblings[index - 1]) && path.length <= LIMITS.depth;
  tools.appendChild(h('button', {
    class: 'btn small icon', type: 'button', title: 'Nest into the element above', 'aria-label': 'Nest into previous',
    disabled: !nestable,
    onclick: () => { const prev = siblings[index - 1]; prev.inputs.push(siblings.splice(index, 1)[0]); rerender(); }
  }, '›'));
  tools.appendChild(h('button', {
    class: 'btn small icon', type: 'button', title: 'Move out one level', 'aria-label': 'Move out one level',
    disabled: path.length < 2,
    onclick: () => {
      const grand = parentArray(path.slice(0, -1));
      const parentIndex = path[path.length - 2];
      grand.splice(parentIndex + 1, 0, siblings.splice(index, 1)[0]);
      rerender();
    }
  }, '‹'));
  tools.appendChild(h('button', {
    class: 'btn small icon danger', type: 'button', title: 'Remove', 'aria-label': 'Remove element',
    onclick: () => { siblings.splice(index, 1); rerender(); }
  }, '×'));

  const parts = [
    h('select', {
      class: 'w-kind', 'aria-label': 'Element kind', onchange: (e) => {
        element.kind = e.target.value;
        element.inputs = element.inputs.slice(0, maxInputs(element));
        element.op = 'on';
        element.value = null;
        element.relay = '';
        rerender();
      }
    }, KINDS.map((k) => h('option', { value: k, text: KIND_LABEL[k] || k, selected: element.kind === k }))),
    h('input', {
      class: 'w-text', type: 'text', 'aria-label': 'Operator sentence', placeholder: 'operator sentence / label',
      value: element.text || '', oninput: (e) => { element.text = e.target.value; markDirty(); }
    })
  ];

  if (isContact(element)) {
    // The row's TAG field picks one of the area's tags by name, or the internal relay it reads.
    // The assignment itself — a name bound to a source and a tag — is made on the Tags tab, the
    // page's one tag editor, so a row never assigns a pair of its own and a signal has one home.
    parts.push(tagPickSelect(element, rerender));
    if (newTagForId === element.id) parts.push(newTagInput(element));
    if (readsRelay(element)) {
      parts.push(h('input', {
        class: 'w-relay mono', type: 'text', 'aria-label': 'Relay', placeholder: 'M01', maxlength: 16,
        value: element.relay || '', oninput: (e) => { element.relay = e.target.value.toUpperCase(); markDirty(); }
      }));
    } else {
      // A row authored before the area's tags existed keeps its direct pair: the pair is shown,
      // kept through a save, and replaced the moment the row picks a named entry.
      if (isStoredPair(element)) {
        parts.push(h('span', {
          class: 'info-mark', title: storedTagHint(element), 'aria-label': 'Direct tag',
          'aria-describedby': 'directTagNote'
        }, 'i'));
        parts.push(h('span', {
          class: 'stored-tag mono small', title: storedTagHint(element),
          text: 'Direct tag: ' + storedTagLabel(element)
        }));
      }
      parts.push(h('select', {
        class: 'w-op', 'aria-label': 'Comparison', onchange: (e) => {
          element.op = e.target.value;
          element.value = element.op === 'gt' || element.op === 'lt' || element.op === 'eq' ? (element.value === null ? 0 : element.value) : null;
          rerender();
        }
      }, OPS.map((o) => h('option', { value: o, text: OP_LABEL[o], selected: (element.op || 'on') === o }))));
      if (element.op === 'gt' || element.op === 'lt' || element.op === 'eq') {
        parts.push(h('input', {
          class: 'w-num mono', type: 'number', step: 'any', 'aria-label': 'Comparison value', placeholder: 'value',
          value: element.value === null || element.value === undefined ? '' : element.value,
          oninput: (e) => { element.value = e.target.value === '' ? null : Number(e.target.value); markDirty(); }
        }));
      }
    }
  }

  if (isCoil(element)) {
    parts.push(h('input', {
      class: 'w-relay mono', type: 'text', 'aria-label': 'Relay the coil drives', placeholder: 'M01', maxlength: 16,
      value: element.relay || '', oninput: (e) => { element.relay = e.target.value.toUpperCase(); markDirty(); }
    }));
  }

  if (isTimer(element)) {
    parts.push(h('input', {
      class: 'w-num mono', type: 'number', min: 0, step: 100, 'aria-label': 'Preset time in milliseconds', placeholder: 'PT ms',
      value: element.ptMs || 0, oninput: (e) => { element.ptMs = Number(e.target.value || 0); markDirty(); }
    }), h('span', { class: 'muted small', text: 'ms' }));
  }

  if (isCounter(element)) {
    parts.push(h('input', {
      class: 'w-num mono', type: 'number', min: 0, step: 1, 'aria-label': 'Counter preset', placeholder: 'PV',
      value: element.pv || 0, oninput: (e) => { element.pv = Number(e.target.value || 0); markDirty(); }
    }), h('span', { class: 'muted small', text: 'PV' }));
  }

  parts.push(h('select', {
    class: 'w-sev', 'aria-label': 'Severity', onchange: (e) => { element.severity = e.target.value; markDirty(); }
  }, SEVERITIES.map((s) => h('option', { value: s, text: SEV_LABEL[s], selected: (element.severity || 'block') === s }))));

  return h('div', { class: 'el-edit' }, parts, h('span', { class: 'spacer' }), tools);
}

// The row's TAG field, the way the HMI's own editor works it: the picker offers the block's area's
// tags by name — assigned on the Tags tab, where a name's source and tag live — the internal relay
// route, and a new name. A new name is authored here and joins the area's list unassigned, so the
// Tags tab is still the one place a source and a tag are chosen. A row never assigns a pair itself.
function tagPickSelect(element, rerender) {
  const named = (element.tag || '').trim();
  const areaName = (blockArea(draft) || '').trim();
  const list = areaTags(areaName);
  // The Tags tab's unsaved list wins while it is the one being edited, so a name just added (or
  // removed) there is offered here before Save list.
  const local = areaName && tagDraft && (tagDraft.name || '').trim().toLowerCase() === areaName.toLowerCase()
    ? tagDraft.tags
    : null;
  const entries = local || (list ? list.tags : []);
  const creating = newTagForId === element.id;
  const direct = !named && !readsRelay(element) && !creating;
  const listed = entries.some((t) => (t.name || '').trim().toLowerCase() === named.toLowerCase());
  return h('select', {
    class: 'w-tag', 'aria-label': 'Tag reference',
    title: 'One of this area\'s tags, a new name (its source and tag are assigned in the Tags tab), or the internal relay it reads.',
    onchange: (e) => {
      const value = e.target.value;
      if (value === '') return;   // the row's current state, shown, not re-selectable
      newTagForId = value === NEW_TAG ? element.id : '';
      if (value === NEW_TAG) { element.tag = ''; element.relay = ''; element.sourceId = ''; element.itemId = ''; }
      else if (value === RELAY) { element.relay = element.relay || 'M01'; element.tag = ''; element.sourceId = ''; element.itemId = ''; }
      else { element.relay = ''; element.tag = value; element.sourceId = ''; element.itemId = ''; }
      rerender();
    }
  },
    named && !listed ? h('option', { value: named, text: named, selected: true }) : null,
    direct ? h('option', { value: '', selected: true, text: isStoredPair(element) ? '(direct tag)' : '' }) : null,
    entries.map((t) =>
      h('option', { value: t.name, text: t.name + (((t.sourceId || '').trim() && (t.itemId || '').trim()) ? '' : ' · unassigned'), selected: named === t.name })),
    h('option', { value: NEW_TAG, selected: creating, text: '+ New tag…' }),
    h('option', { value: RELAY, selected: readsRelay(element), text: '(internal relay)' })
  );
}

// The name typed in the TAG picker's new-tag route. Typing keeps the row in step even when Save
// is pressed before the box loses focus; committing puts the name on the area's list unassigned.
function newTagInput(element) {
  const input = h('input', {
    class: 'w-tag-new mono', type: 'text', 'aria-label': 'New tag name', placeholder: 'NEW_TAG', maxlength: 64,
    value: element.tag || '',
    oninput: (e) => {
      element.tag = e.target.value;
      element.sourceId = '';
      element.itemId = '';
      element.relay = '';
      markDirty();
    },
    onchange: (e) => {
      const name = e.target.value.trim();
      if (!name) return;
      element.tag = name;
      newTagForId = '';
      rememberTagName(name);
    }
  });
  setTimeout(() => input.focus(), 0);
  return input;
}

// A name authored in the Editor joins the block's area's list as an unassigned entry, so the Tags
// tab shows it ready for its source and tag. The list itself is saved on the Tags tab.
function rememberTagName(name) {
  const area = (blockArea(draft) || '').trim();
  if (!area) return;
  syncTagDraft();
  if (!tagDraft || (tagDraft.name || '').trim().toLowerCase() !== area.toLowerCase()) return;
  const key = name.trim().toLowerCase();
  if (!key || tagDraft.tags.some((t) => (t.name || '').trim().toLowerCase() === key)) return;
  tagDraft.tags.push({ name: name.trim(), sourceId: '', itemId: '' });
  rememberTagDraft();
}

// A row that still carries the legacy direct pair — a source and a tag on the element itself
// instead of a name in the area's tags. It is shown as it is and kept through a save (nothing the
// bridge still evaluates is dropped); picking a named entry is what moves it into the area's list.
function isStoredPair(element) {
  return (((element.sourceId || '').trim() + (element.itemId || '').trim()).length > 0);
}

function storedTagLabel(element) {
  const label = tagLabel(element);
  if (label) return label;
  return ((element.sourceId || '') + ' / ' + (element.itemId || '')).trim().replace(/^\W+|\W+$/g, '');
}

// The HMI's own sentence, so the plant reads the same thing on both surfaces.
const storedTagHint = (element) =>
  'Direct tag: ' + storedTagLabel(element) + ' — add it to this area\'s tags (the Tags tab) to assign it there.';

function renderSaveBarNode() {
  return h('div', { class: 'savebar', id: 'savebar' },
    h('button', { class: 'btn primary', type: 'button', id: 'btnSave', disabled: !dirty, onclick: saveBlock }, 'Save'),
    h('button', {
      class: 'btn', type: 'button', id: 'btnRevert', disabled: !dirty,
      onclick: () => { const fresh = defs.blocks.find((b) => b.id === draft.id); if (fresh) { draft = clone(fresh); draftForm = formOf(fresh); dirty = false; renderEditor(); } }
    }, 'Revert'),
    deleteControl(),
    h('span', { class: 'grow' }),
    h('span', { class: 'msg', text: dirty ? 'Unsaved changes.' : 'Saved state.' })
  );
}

// Deleting is a two-step, in-page action rather than a confirm() dialog: a blocking dialog
// freezes the page (and anything driving it, the HMI's WebView included), and the button carries
// the same intent either way. The first press arms it, the second deletes.
function deleteControl() {
  const button = h('button', { class: 'btn danger', type: 'button', id: 'btnDelete' }, 'Delete block');
  let armed = false;
  let timer = null;
  const disarm = () => {
    armed = false;
    clearTimeout(timer);
    button.classList.remove('armed');
    button.textContent = 'Delete block';
  };
  button.addEventListener('click', () => {
    if (!armed) {
      armed = true;
      button.classList.add('armed');
      button.textContent = 'Confirm delete';
      setSaveMessage('Press Confirm delete to remove "' + draft.name + '" from the bridge.', 'warn');
      timer = setTimeout(disarm, 6000);
      return;
    }

    disarm();
    deleteBlock(draft);
  });
  return button;
}

function setSaveMessage(text, kind) {
  const msg = document.querySelector('#savebar .msg');
  if (!msg) { if (kind === 'err') toast(text, 'err'); return; }
  msg.dataset.keep = kind === 'ok' ? '' : '1';
  msg.className = 'msg' + (kind ? ' ' + kind : '');
  msg.textContent = text;
}

// ---------------------------------------------------------------- tabs

function renderTabs() {
  const labels = { editor: 'Editor', tags: 'Tags', result: 'Result' };
  return h('div', { class: 'tabs', role: 'tablist' },
    ['editor', 'tags', 'result'].map((id) => h('button', {
      class: 'tab' + (tab === id ? ' on' : ''), type: 'button', role: 'tab',
      'aria-selected': tab === id ? 'true' : 'false',
      onclick: () => { tab = id; renderEditor(); }
    }, labels[id])));
}

// ---------------------------------------------------------------- Result tab

// Definitions by element id, and the elements belonging to each step of a sequence: the
// evaluation arrives as one flat list, so a sequence is grouped back into its steps by the
// definitions' own ids (the same join the retired Logic web app and the HMI do). Conditions and
// elements share one id space — a contact keeps its condition's id.
function indexBlock(block) {
  const byId = new Map();
  const stepIds = new Map();
  const add = (list, stepId) => {
    for (const item of list || []) {
      byId.set(item.id, item);
      if (stepId) {
        if (!stepIds.has(stepId)) stepIds.set(stepId, []);
        stepIds.get(stepId).push(item.id);
      }

      add(item.inputs, stepId);
    }
  };
  add(block.conditions, null);
  add(block.elements, null);
  for (const step of block.steps || []) {
    add(step.conditions, step.id);
    add(step.elements, step.id);
  }

  return { byId, stepIds };
}

// Which step each evaluated element belongs to. An element expanded from a step's flat conditions
// has no id of its own, so it belongs to the step of the next known id — a wrapper always precedes
// what it wraps (the retired app's own rule).
function stepAssignments(block, elements) {
  const stepById = new Map();
  (block.steps || []).forEach((step, index) => {
    const own = new Map();
    const walk = (list) => {
      for (const item of list || []) {
        own.set(item.id, true);
        walk(item.inputs);
      }
    };
    walk(step.conditions);
    walk(step.elements);
    for (const id of own.keys()) stepById.set(id, index);
  });

  const assigned = (elements || []).map(() => -1);
  let next = -1;
  for (let i = assigned.length - 1; i >= 0; i--) {
    const known = stepById.get(elements[i].id);
    if (known !== undefined) next = known;
    assigned[i] = next;
  }

  let previous = -1;
  assigned.forEach((value, i) => {
    if (value < 0) assigned[i] = previous; else previous = value;
  });
  return assigned;
}

// How many steps are done, and which one is running — the progress line the old app showed.
function sequenceProgress(block, state) {
  const steps = block.steps || [];
  if (!steps.length) return '';
  const words = new Map(((state && state.steps) || []).map((s) => [s.id, s.state]));
  let done = 0;
  let current = '';
  steps.forEach((step, index) => {
    const word = words.get(step.id) || '';
    if (word === 'done') done += 1;
    else if (!current && (word === 'current' || word === 'unknown')) current = step.name || ('Step ' + (index + 1));
  });

  if (done === steps.length) return 'complete';
  return done + '/' + steps.length + ' steps' + (current ? ' · ' + current : '');
}

const stateWord = (state) => (state === 'true' ? '1' : state === 'false' ? '0' : '—');
const statePill = (state) => (state === 'true' ? 'ok' : state === 'false' ? 'bad' : 'warn');

// Block and step words, from the HMI's LogicText so every surface reads the same: the state word
// always sits next to the colour, never colour alone.
const BLOCK_KIND_LABEL = { interlock: 'Interlock', permissive: 'Permissive', sequence: 'Sequence' };
const blockKindLabel = (kind) => BLOCK_KIND_LABEL[(kind || '').toLowerCase()] || 'Interlock';

function blockStateWord(state) {
  const word = (state || '').toLowerCase();
  if (word === 'ready') return 'READY';
  if (word === 'blocked') return 'BLOCKED';
  if (word === 'disabled') return 'DISABLED';
  return 'NO DATA';
}

const blockStatePill = (state) => (state === 'ready' ? 'ok' : state === 'blocked' ? 'bad' : state === 'disabled' ? 'muted' : 'warn');

// Contact forms and function-block input pins, the way the HMI's LogicText labels a row: a
// contact reads as the form it takes (NO / NC / > / < / =), a block as the input it watches
// (TON IN, CTU CU, SR S1 …). A coil is just COIL — its relay is named in the tag line.
const PIN_LABEL = {
  and: 'AND', or: 'OR', xor: 'XOR', not: 'NOT',
  ton: 'TON IN', tof: 'TOF IN', tp: 'TP IN',
  ctu: 'CTU CU', ctd: 'CTD CD', sr: 'SR S1', rs: 'RS S', r_trig: 'R_TRIG IN', f_trig: 'F_TRIG IN'
};

function kindLabelFor(state, def) {
  const kind = state.kind || 'contact';
  if (kind === 'contact') {
    const op = (def && def.op) || (state.should === '0' ? 'off' : state.should === '1' ? 'on' : '');
    if (op === 'on') return 'NO';
    if (op === 'off') return 'NC';
    if (op === 'gt') return '>';
    if (op === 'lt') return '<';
    if (op === 'eq') return '=';
    return 'NO';
  }

  if (kind === 'coil') return 'COIL';
  return PIN_LABEL[kind] || kind;
}

// The bit the row reads: an NO contact is satisfied by 1, an NC contact by 0, so the actual column
// shows the state of the requirement, not of the tag. A comparison keeps its own value.
function actualBit(state, def) {
  const kind = state.kind || 'contact';
  if (kind !== 'contact') return stateWord(state.state);
  const op = (def && def.op) || (state.should === '0' ? 'off' : 'on');
  if (op === 'on' || op === 'off') {
    if (state.matches === null || state.matches === undefined) return '?';
    const bit = state.matches ? 1 : 0;
    return String(op === 'on' ? bit : 1 - bit);
  }

  const value = state.valueText || '';
  return value && value !== '—' ? value : '?';
}

// Seconds with the trailing zeros trimmed, as the old app printed them.
const secondsText = (ms) => (Number(ms || 0) / 1000).toFixed(2).replace(/\.?0+$/, '') + ' s';

// Timers and counters report what they are doing, not just their outputs.
function progressLine(state, def) {
  const kind = state.kind;
  if (kind === 'ton' || kind === 'tof' || kind === 'tp') {
    const pt = Number(state.ptMs || 0);
    if (pt <= 0) return '';
    const verb = kind === 'ton' ? 'holding' : kind === 'tof' ? 'off-delay' : 'pulse';
    if (state.state === 'true') return verb + ' done · ' + secondsText(pt);
    return verb + ' ' + secondsText(state.elapsedMs) + ' of ' + secondsText(pt);
  }

  if (kind === 'ctu' || kind === 'ctd') {
    // The count is the live CV; the preset lives on the definition.
    return 'count ' + Number(state.count || 0) + ' of ' + Number((def && def.pv) || 0);
  }

  return '';
}

// The comparison the operator reads: what the row needs against what it has.
function shouldLine(state, def) {
  if (!state.should) return '';
  return 'should ' + state.should + ' · actual ' + actualBit(state, def);
}

// Where the element's reading comes from, in the operator's words, the way the HMI builds it: a
// relay element names its relay where a tag contact names its tag (the mapped tag's display name
// and its source's name), a named entry also shows the tag it is bound to, and an entry that
// cannot resolve says which of the three reasons applies.
function tagLabel(def) {
  if (!def) return '';
  if ((def.relay || '').trim()) return 'relay ' + def.relay.trim();

  const name = (def.tag || '').trim();
  if (name) {
    const areaName = blockArea(draft);
    const list = areaTags(areaName);
    const entry = (list && list.tags || []).find((t) => (t.name || '').trim().toLowerCase() === name.toLowerCase());
    if (!list || !entry) {
      return name + (areaName ? ' (not in ' + areaName + ')' : ' (no area)');
    }

    if (!(entry.sourceId || '').trim() || !(entry.itemId || '').trim()) return name + ' (not assigned)';
    return name + ' · ' + catalogLabel(entry.sourceId, entry.itemId);
  }

  return def.itemId ? catalogLabel(def.sourceId, def.itemId) : '';
}

// A stored (sourceId, itemId) pair as the plant reads it: the tag's display name and its source's
// name, else the item id alone — the HMI catalog's own label.
function catalogLabel(sourceId, itemId) {
  const tag = catalogTag(sourceId, itemId);
  if (!tag) return itemId;
  const label = (tag.displayName || '').trim() || itemId;
  return (tag.sourceName || '').trim() ? label + ' · ' + tag.sourceName.trim() : label;
}

// One evaluated element, the way the retired Logic app presented it: the mark, the form it takes,
// the sentence that explains it, the bit it reads and the raw value behind it, then the small
// print (the comparison, progress, the tag, what to do next).
function resultRow(state, def) {
  const row = h('div', { class: 'rl', 'data-id': state.id, 'data-live': '1', style: '--depth:' + (state.depth || 0) });
  const card = h('div', { class: 'rl-card ' + statePill(state.state) },
    h('div', { class: 'rl-head' },
      h('span', { class: 'rl-mark', text: state.state === 'true' ? '✓' : state.state === 'false' ? '✗' : '—' }),
      h('span', { class: 'rl-kind', text: kindLabelFor(state, def) }),
      h('span', { class: 'rl-text', text: def ? def.text : '' },
        def && def.severity === 'warn' ? h('span', { class: 'warnline', text: ' warn only' }) : null),
      h('span', { class: 'rl-actual', text: actualBit(state, def) }),
      h('span', { class: 'rl-value mono small', text: state.valueText || '—' })));
  card.__state = state;
  card.__def = def;

  const comparison = shouldLine(state, def);
  if (comparison) {
    card.appendChild(h('span', {
      class: 'rl-line rl-should ' + (state.matches === true ? 'ok' : state.matches === false ? 'bad' : 'unknown'),
      text: comparison
    }));
  }

  const progress = progressLine(state, def);
  if (progress) card.appendChild(h('span', { class: 'rl-line rl-progress warn', text: progress }));

  const label = tagLabel(def);
  if (label) card.appendChild(h('span', { class: 'rl-line rl-tag muted', text: label }));

  if (def && def.nextStepText && state.state !== 'true') {
    card.appendChild(h('span', { class: 'rl-line warn', text: def.nextStepText }));
  }

  row.appendChild(card);
  return row;
}

const STEP_WORD = { done: 'Done', current: 'Current', pending: 'Pending', unknown: 'No data' };

// The evaluation arrives as one flat list with a depth per element. It is written nested — one node
// per element, the deeper ones under the one above them — the way the editor's own network is, so
// the row that owns what is on screen can be read from the rows (see syncNetPinning). Flat, no row
// could say whose child it was, and a parent scrolled away could not be handed back.
function resultTree(elements) {
  const roots = [];
  const open = [];
  for (const element of elements) {
    // A depth deeper than the elements before it allow is read as one level down: the bridge names
    // a parent before its child, and a stray depth must not break the list open.
    const depth = Math.max(0, Math.min(element.depth || 0, open.length));
    const node = { element, children: [] };
    (depth ? open[depth - 1].children : roots).push(node);
    open.length = depth;
    open.push(node);
  }
  return roots;
}

function resultNode(node, index) {
  const box = h('div', { class: 'rl-node' });
  box.appendChild(resultRow(node.element, index.byId.get(node.element.id)));
  if (!node.children.length) return box;
  const kids = h('div', { class: 'rl-kids' });
  for (const child of node.children) kids.appendChild(resultNode(child, index));
  box.appendChild(kids);
  return box;
}

function resultNet(elements, index) {
  const net = h('div', { class: 'net' });
  for (const node of resultTree(elements)) net.appendChild(resultNode(node, index));
  return net;
}

function resultSteps(state, elements, index) {
  const assignments = stepAssignments(draft, elements);
  const stepStates = new Map(((state && state.steps) || []).map((s) => [s.id, s]));
  const box = h('div', { class: 'net' });
  (draft.steps || []).forEach((step, stepIndex) => {
    const stepState = stepStates.get(step.id);
    const word = stepState ? stepState.state : 'unknown';
    const card = h('div', { class: 'step-card' + (word === 'current' ? ' current' : '') },
      h('div', { class: 'rl-head' },
        h('span', { class: 'rl-text', text: step.name || ('Step ' + (stepIndex + 1)) }),
        h('span', {
          class: 'pill step-pill ' + (word === 'done' ? 'ok' : word === 'current' ? 'warn' : 'muted'),
          'data-step': step.id, text: STEP_WORD[word] || word
        })),
      stepState && stepState.reason ? h('span', { class: 'rl-line rl-step-reason muted', text: stepState.reason }) : null);

    const stepElements = elements.filter((element, i) => assignments[i] === stepIndex);
    if (stepElements.length) card.appendChild(resultNet(stepElements, index));
    else card.appendChild(h('span', { class: 'rl-line muted', text: 'No evaluated elements.' }));
    box.appendChild(card);
  });

  return box;
}

function renderResultTab(editor) {
  const state = blockStates.get(draft.id);
  const index = indexBlock(draft);
  const elements = (state && state.elements) || [];
  const word = state ? state.state : 'unknown';
  const progress = sequenceProgress(draft, state);

  // The tag lines read as the plant's own names, which come from the bridge's tag snapshot — the
  // same one the Tags tab's picker uses. Until it arrives the rows show the raw pair.
  if (!catalog) loadCatalog().then(repaintResultTags);

  editor.appendChild(h('div', { class: 'res-head' },
    h('div', { class: 'res-title' },
      h('span', { class: 'res-name', text: draft.name || '(unnamed)' }),
      h('span', { class: 'rl-kind', text: blockKindLabel(draft.kind) }),
      h('span', { class: 'pill', id: 'resStamp', role: 'status', text: blockStateWord(word) }),
      h('span', { class: 'res-fresh mono small', id: 'resFresh', text: '' })),
    draft.description ? h('p', { class: 'muted small', text: draft.description }) : null,
    progress ? h('p', { class: 'rl-line warn', id: 'resProgress', text: progress }) : null,
    h('p', { class: 'res-reason', id: 'resReason', text: '' })));
  // The verdict rides the scroll with the rows, and keeps the parent of what is on screen — the
  // editor's own treatment, on the tab that is read rather than authored.
  editor.appendChild(renderPin());

  if (!state) {
    // Four different facts, never one sentence: an empty store, a draft that was never saved, a
    // bridge that cannot be read, and a read still in flight.
    const empty = !defs.blocks.length
      ? ['NO BLOCKS YET', 'Use + New to author the first one.']
      : dirty
        ? ['NOT SAVED YET', 'Save the block to see its evaluated network.']
        : feedState === 'failed'
          ? ['COULD NOT READ', 'The bridge is unreachable — its evaluation will appear when it answers.']
          : ['READING…', 'Reading the bridge\'s evaluation…'];
    editor.appendChild(h('p', { class: 'section-title', text: empty[0] }));
    editor.appendChild(h('p', { class: 'muted small', text: empty[1] }));
  } else if (draftForm === 'steps') {
    editor.appendChild(h('div', { class: 'section-title', text: 'Steps' }));
    editor.appendChild(resultSteps(state, elements, index));
  } else if (!elements.length) {
    editor.appendChild(h('p', { class: 'section-title', text: 'NOTHING TO EVALUATE' }));
    editor.appendChild(h('p', { class: 'muted small', text: 'This block has no elements to evaluate yet.' }));
  } else {
    editor.appendChild(h('div', { class: 'section-title', text: 'Network' + (draftForm === 'conditions' ? ' (the bridge\'s expansion of the flat conditions)' : '') }));
    editor.appendChild(resultNet(elements, index));
  }

  editor.appendChild(h('p', { class: 'muted small', text: 'definitions v' + version + ' · ' + elements.length + ' elements evaluated' }));
  refreshResultHeader();
}

// The verdict, reason and freshness of the Result tab's header, refreshed on every poll so the
// rows are never rewritten (only their text is).
function refreshResultHeader() {
  const stamp = el('resStamp');
  if (!stamp || !draft) return;
  const state = blockStates.get(draft.id);
  const word = state ? state.state : 'unknown';
  stamp.className = 'pill ' + blockStatePill(word);
  stamp.textContent = blockStateWord(word);

  const progress = el('resProgress');
  if (progress) progress.textContent = sequenceProgress(draft, state);

  const reason = el('resReason');
  if (reason) {
    const why = state && state.reason ? state.reason : '';
    reason.textContent = !state ? ''
      : word === 'ready' ? 'all conditions met'
        : word === 'blocked' || word === 'unknown' ? (why ? 'Blocked by: ' + why : '')
          : why;
    reason.className = 'res-reason ' + (word === 'ready' ? 'ok' : word === 'blocked' ? 'bad' : 'warn');
  }

  const fresh = el('resFresh');
  if (fresh) {
    const text = freshnessText();
    fresh.textContent = text;
    fresh.className = 'res-fresh mono small' + (text.startsWith('stale') ? ' stale' : '');
    fresh.title = lastEvaluated ? 'evaluated ' + formatTime(lastEvaluated) : '';
  }

  // What the rows ride under is this head, and the poll writes text into it (the reason, the
  // progress): its height is re-measured here, so the line the strip rides never goes stale.
  syncNetPinning();
}

// The freshness line, the HMI's own wording: the feed's own state wins over the snapshot's age, so
// a dead link never keeps claiming a recent update and a quiet bridge reads as quiet.
function freshnessText() {
  if (feedState === 'failed') return 'stale — bridge unreachable';
  if (feedState === 'reconnecting') return 'stale — reconnecting';
  if (!lastEvaluated) return 'no state yet';

  const age = (Date.now() - lastEvaluated.getTime()) / 1000;
  if (age < 5) return 'updated just now';
  if (age < 60) return 'updated ' + Math.floor(age) + 's ago';
  if (age < 3600) return 'updated ' + Math.floor(age / 60) + 'm ago';
  return 'updated ' + Math.floor(age / 3600) + 'h ago';
}

function formatTime(date) {
  const pad = (value, size) => String(value).padStart(size, '0');
  return pad(date.getHours(), 2) + ':' + pad(date.getMinutes(), 2) + ':' + pad(date.getSeconds(), 2) + '.' + pad(date.getMilliseconds(), 3);
}

// ---------------------------------------------------------------- Tags tab

// Every name a block's rows reference, in their original spelling — conditions, element
// contacts, step handshakes and actions — deduplicated case-insensitively. The Tags tab lists
// them so a name typed in the Editor always has a row where its source and tag are assigned.
function referencedTagNames(block) {
  const names = [];
  const seen = new Set();
  const add = (name) => {
    const trimmed = (name || '').trim();
    if (!trimmed) return;
    const key = trimmed.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    names.push(trimmed);
  };
  const walk = (list) => {
    for (const element of list || []) {
      add(element.tag);
      walk(element.inputs);
    }
  };
  for (const condition of block.conditions || []) add(condition.tag);
  walk(block.elements);
  for (const step of block.steps || []) {
    add(step.completionTag);
    for (const condition of step.conditions || []) add(condition.tag);
    walk(step.elements);
  }
  for (const action of block.actions || []) add(action.tag);
  return names;
}

function usedNames(block) {
  return new Set(referencedTagNames(block).map((name) => name.toLowerCase()));
}

function loadCatalog() {
  return fetchJson('/api/hmi/tags').then((body) => {
    catalog = body.tags || [];
    catalogFetchedAt = Date.now();
    const bySource = new Map();
    for (const tag of catalog) {
      if (!bySource.has(tag.sourceId)) bySource.set(tag.sourceId, { id: tag.sourceId, name: tag.sourceName || tag.sourceId, tags: [] });
      bySource.get(tag.sourceId).tags.push(tag);
    }
    catalogSources = [...bySource.values()].sort((a, b) => a.name.localeCompare(b.name));
    for (const source of catalogSources) source.tags.sort((a, b) => (a.displayName || a.itemId).localeCompare(b.displayName || b.itemId));
    return catalog;
  }).catch((error) => {
    catalog = [];
    catalogSources = [];
    toast('Could not read the bridge\'s tags: ' + error.message, 'err');
    return [];
  });
}

const catalogTag = (sourceId, itemId) =>
  (catalog || []).find((t) => t.sourceId === sourceId && t.itemId === itemId) || null;

// The Tags tab's live chip, the way the HMI's LogicText.TagLiveChip reads it: bad quality says so,
// a bit reads 1 / 0, a number keeps up to three decimals and its unit, and a tag that is not
// assigned or has never delivered reads "—".
function liveText(tag) {
  if (!tag) return '—';
  if (tag.isGood === false) return 'bad';
  const value = tag.value;
  if (value === null || value === undefined) return '—';

  let text;
  if (typeof value === 'boolean') text = value ? '1' : '0';
  else if (typeof value === 'number') text = Number.isInteger(value) ? String(value) : value.toFixed(3).replace(/\.?0+$/, '');
  else text = String(value);

  return tag.unit && tag.unit.trim() ? text + ' ' + tag.unit.trim() : text;
}

function tagEntryRow(entry, index, used) {
  const sourceOption = (sourceId) => catalogSources.find((s) => s.id === sourceId);
  const sourceTags = sourceOption(entry.sourceId);
  const row = h('div', { class: 'tag-row' });

  row.appendChild(h('input', {
    class: 'tag-name mono', type: 'text', 'aria-label': 'Tag name', placeholder: 'START_PB', maxlength: 32,
    value: entry.name || '',
    oninput: (e) => { entry.name = e.target.value; }
  }));

  row.appendChild(h('select', {
    class: 'tag-source', 'aria-label': 'Source', onchange: (e) => {
      entry.sourceId = e.target.value;
      entry.itemId = '';
      renderEditor();
    }
  },
    h('option', { value: '', text: '— source —', selected: !entry.sourceId }),
    entry.sourceId && !sourceOption(entry.sourceId) ? h('option', { value: entry.sourceId, text: entry.sourceId + ' (gone)', selected: true }) : null,
    catalogSources.map((source) => h('option', { value: source.id, text: source.name, selected: source.id === entry.sourceId }))));

  row.appendChild(h('div', { class: 'tag-cell' },
    h('select', {
      class: 'tag-item', 'aria-label': 'Tag', onchange: (e) => { entry.itemId = e.target.value; renderEditor(); }
    },
      h('option', { value: '', text: '— tag —', selected: !entry.itemId }),
      entry.itemId && !(sourceTags && sourceTags.tags.some((t) => t.itemId === entry.itemId))
        ? h('option', { value: entry.itemId, text: entry.itemId + ' (not a mapped tag)', selected: true }) : null,
      (sourceTags ? sourceTags.tags : []).map((tag) => h('option', {
        value: tag.itemId, selected: tag.itemId === entry.itemId,
        text: (tag.displayName || tag.itemId) + (tag.unit && tag.unit.trim() ? ' · ' + tag.unit : '')
      }))),
    // The HMI's own note: a tag that is not mapped for writing cannot carry an action.
    (() => {
      const bound = catalogTag(entry.sourceId, entry.itemId);
      return bound && bound.writeable === false
        ? h('span', { class: 'warnline', text: 'read-only tag — an action cannot write it.' })
        : null;
    })()));

  row.appendChild(h('span', { class: 'tag-live mono', 'data-live-tag': entry.sourceId + '|' + entry.itemId, text: liveText(catalogTag(entry.sourceId, entry.itemId)) }));
  row.appendChild(h('span', {
    class: 'pill ' + (used.has((entry.name || '').trim().toLowerCase()) ? 'ok' : 'muted'),
    text: used.has((entry.name || '').trim().toLowerCase()) ? 'used' : '',
    title: 'Used by the block open in the Editor tab'
  }));
  row.appendChild(h('button', {
    class: 'btn small icon danger', type: 'button', 'aria-label': 'Remove tag',
    onclick: () => { tagDraft.tags.splice(index, 1); renderEditor(); }
  }, '×'));
  return row;
}

// The Tags tab is the page's assignment surface: the block's own AREA, the area's shared list of
// named tags (each bound to its source and tag), and the save of both. The Editor tab's rows pick
// a name from that list — they never carry a pair of their own — so a signal is assigned here and
// only here, and every block of the area reads the same binding.
function renderTagsTab(editor) {
  syncTagDraft();

  if (!catalog) {
    editor.appendChild(h('p', { class: 'muted small', text: 'Reading the bridge\'s tags…' }));
    loadCatalog().then(() => { if (tab === 'tags') renderEditor(); });
  }

  editor.appendChild(renderTagsToolbar());
  const body = h('div', { id: 'tagBody' });
  editor.appendChild(body);
  fillTagBody(body);
  editor.appendChild(renderSaveBarNode());
}

function renderTagsToolbar() {
  const known = Array.from(new Set([...(defs.tagSources || []).map((a) => a.name), blockArea(draft)]
    .filter((n) => n && n.trim()))).sort((a, b) => a.localeCompare(b));

  return h('div', { class: 'tag-toolbar' },
    h('label', { for: 'fArea', class: 'rl-kind', text: 'AREA' }),
    // The block's own area: typing it switches the list below to that area's tags, and the block
    // is what stores the name (Save), so the area and the list it reads can never disagree.
    h('input', {
      type: 'text', id: 'fArea', class: 'tag-area', list: 'areaNames', value: draft.area || '',
      maxlength: 40, placeholder: 'DRY END', 'aria-label': 'Area',
      oninput: (e) => { draft.area = e.target.value; markDirty(); refreshTagList(); }
    }),
    h('datalist', { id: 'areaNames' }, known.map((name) => h('option', { value: name }))),
    h('span', { class: 'grow' }),
    h('button', {
      class: 'btn', type: 'button', id: 'btnNewTag', disabled: !tagArea,
      onclick: () => { tagDraft.tags.push({ name: '', sourceId: '', itemId: '' }); renderEditor(); }
    }, '+ New tag'),
    h('button', {
      class: 'btn', type: 'button', id: 'btnPickTags', disabled: !tagArea || !catalog, onclick: openTagPicker
    }, 'From bridge…'),
    deleteAreaTagsControl(),
    h('button', {
      class: 'btn primary', type: 'button', id: 'btnSaveList', disabled: !tagArea, onclick: saveAreaTags
    }, 'Save list'));
}

// A name the block references but the list does not hold yet (typed in the Editor, or saved by
// another client) joins the list as an unassigned entry, so opening the Tags tab always shows a
// row where its source and tag can be assigned. Existing entries keep their spelling.
function mergeReferencedTagNames() {
  if (!tagArea || !draft || !tagDraft) return;
  const held = new Set(tagDraft.tags
    .map((t) => (t.name || '').trim().toLowerCase())
    .filter((name) => name));
  for (const name of referencedTagNames(draft)) {
    if (held.has(name.toLowerCase())) continue;
    tagDraft.tags.push({ name, sourceId: '', itemId: '' });
    held.add(name.toLowerCase());
  }
}

// The area's list and, under it, the block's own rows: rebuilt in place while the area's name is
// typed, so the box keeps the focus and the table follows the area as it is named.
function fillTagBody(body) {
  if (tagArea) mergeReferencedTagNames();
  if (!tagArea) {
    body.appendChild(h('p', { class: 'warnline', text: 'No area yet — type one above (e.g. DRY END) so this block\'s named tags resolve.' }));
  } else {
    body.appendChild(h('p', { class: 'muted small', text: 'Every ' + tagArea + ' block shares these tags — assign a tag here and every one of their rows reads it.' }));
    if (!(draft.area || '').trim()) {
      // The legacy fallbacks: a block with no area of its own resolves through the name it stores
      // (its group, or a tag source an older client wrote). Saying so is what makes it fixable.
      body.appendChild(h('p', { class: 'warnline', text: 'This block has no AREA of its own: it reads "' + tagArea + '" through its group or the name it stores. Type the area above to make the sharing explicit.' }));
    }
  }

  if (tagArea) {
    const used = usedNames(draft);
    if (!tagDraft.tags.length) {
      body.appendChild(h('p', { class: 'muted small', text: 'No tags yet — add one with New tag, name it and assign its source and tag; every block\'s rows pick it from their TAG list.' }));
    } else {
      body.appendChild(h('div', { class: 'tag-head' },
        h('span', { text: 'NAME' }), h('span', { text: 'SOURCE' }), h('span', { text: 'TAG' }),
        h('span', { text: 'LIVE' }), h('span', { text: 'USED' }), h('span')));
      const list = h('div', { class: 'tag-list' });
      tagDraft.tags.forEach((entry, index) => list.appendChild(tagEntryRow(entry, index, used)));
      body.appendChild(list);

      const unbound = tagDraft.tags.filter((t) => !(t.sourceId || '').trim() || !(t.itemId || '').trim()).length;
      const unnamed = tagDraft.tags.filter((t) => !(t.name || '').trim()).length;
      if (unnamed) body.appendChild(h('p', { class: 'warnline', text: unnamed + ' entr' + (unnamed === 1 ? 'y has' : 'ies have') + ' no name — a block cannot reference it.' }));
      if (unbound) body.appendChild(h('p', { class: 'muted small', text: unbound + ' entr' + (unbound === 1 ? 'y is' : 'ies are') + ' not assigned yet: its readers evaluate unknown, never false.' }));
    }
  }
}

// Typing the area re-resolves the edited list in place. The draft of the area being left is kept
// (tagDrafts), so stepping to another area — or a name typed letter by letter — drops nothing.
function refreshTagList() {
  syncTagDraft();
  const body = el('tagBody');
  if (!body) { renderEditor(); return; }
  body.textContent = '';
  fillTagBody(body);

  const gate = !tagArea;
  for (const id of ['btnNewTag', 'btnDeleteTags', 'btnSaveList']) {
    const button = el(id);
    if (button) button.disabled = gate;
  }
  const pick = el('btnPickTags');
  if (pick) pick.disabled = gate || !catalog;
}

// The block's own area decides which list is edited; the names are matched case-insensitively,
// the way the store and the evaluator match them.
function syncTagDraft() {
  const next = (blockArea(draft) || '').trim();
  if (tagDraft && tagDraft.name.trim().toLowerCase() === next.toLowerCase()) return;
  if (tagDraft) rememberTagDraft();

  const list = areaTags(next);
  tagDraft = list ? { name: list.name, tags: clone(list.tags || []) } : { name: next, tags: [] };
  const held = tagDrafts.get(next.toLowerCase());
  if (held) tagDraft = clone(held);
  tagArea = tagDraft.name.trim() || next;
}

// A list's unsaved edits are kept per area — and a draft that matches the store is forgotten — so
// nothing the author typed is lost by stepping to another area and back.
function rememberTagDraft() {
  const key = (tagDraft.name || '').trim().toLowerCase();
  if (!key) return;
  const stored = areaTags(tagDraft.name);
  if (JSON.stringify((stored && stored.tags) || []) === JSON.stringify(tagDraft.tags || [])) tagDrafts.delete(key);
  else tagDrafts.set(key, clone(tagDraft));
}

function saveAreaTags() {
  if (!tagDraft || !tagArea) return;
  fetchJson('/api/logic/tagsources', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tagSource: { name: tagArea, tags: tagDraft.tags } })
  })
    .then((body) => {
      const saved = body.tagSource;
      const index = (defs.tagSources || []).findIndex((a) => (a.name || '').trim().toLowerCase() === (saved.name || '').trim().toLowerCase());
      if (index >= 0) defs.tagSources[index] = saved; else defs.tagSources.push(saved);
      areas = defs.tagSources;
      tagDraft = { name: saved.name, tags: clone(saved.tags || []) };
      tagArea = saved.name.trim();
      tagDrafts.delete(tagArea.toLowerCase());
      version = body.version || version;
      renderEditor();
      toast((saved.tags || []).length + ' tags now shared by "' + saved.name + '".', 'ok');
    })
    .catch((error) => toast(error.message, 'err'));
}

// Deleting an area's list is a two-step, in-page action like the block delete; the bridge
// refuses while blocks read the list, and its message names how many would break.
function deleteAreaTagsControl() {
  const button = h('button', { class: 'btn danger', type: 'button', id: 'btnDeleteTags', disabled: !tagArea }, 'Delete area tags');
  let armed = false;
  let timer = null;
  const disarm = () => { armed = false; clearTimeout(timer); button.classList.remove('armed'); button.textContent = 'Delete area tags'; };
  button.addEventListener('click', () => {
    if (!armed) {
      armed = true;
      button.classList.add('armed');
      button.textContent = 'Confirm delete';
      timer = setTimeout(disarm, 6000);
      return;
    }

    disarm();
    fetchJson('/api/logic/tagsources/' + encodeURIComponent(tagArea), { method: 'DELETE' })
      .then((body) => {
        defs.tagSources = (defs.tagSources || []).filter((a) => (a.name || '').trim().toLowerCase() !== tagArea.trim().toLowerCase());
        areas = defs.tagSources;
        version = body.version || version;
        tagDrafts.delete(tagArea.trim().toLowerCase());
        tagDraft = { name: tagArea, tags: [] };
        renderEditor();
        toast('Deleted the tags of ' + tagArea, 'ok');
      })
      .catch((error) => toast(error.message, 'err'));
  });
  return button;
}

// A name a block can carry: upper case, word characters only, bounded like the store bounds it.
function tagNameFor(displayName, itemId, taken) {
  const base = (displayName || itemId || 'TAG').toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 30) || 'TAG';
  let name = base;
  let n = 2;
  while (taken.has(name.toLowerCase())) name = base.slice(0, 28) + '_' + n++;
  taken.add(name.toLowerCase());
  return name;
}

function openTagPicker() {
  const overlay = h('div', { class: 'overlay' });
  const selected = new Set();
  const list = h('div', { class: 'pick-list' });
  const addButton = h('button', { class: 'btn primary', type: 'button', disabled: true }, 'Add selected');

  const paint = () => {
    list.textContent = '';
    const needle = (search.value || '').trim().toLowerCase();
    const held = new Set(tagDraft.tags
      .filter((t) => (t.sourceId || '').trim() && (t.itemId || '').trim())
      .map((t) => t.sourceId + '|' + t.itemId));
    for (const source of catalogSources) {
      const tags = source.tags.filter((tag) => !needle
        || (tag.displayName || '').toLowerCase().includes(needle)
        || tag.itemId.toLowerCase().includes(needle)
        || source.name.toLowerCase().includes(needle));
      if (!tags.length) continue;
      list.appendChild(h('div', { class: 'pick-group', text: source.name + ' (' + tags.length + ')' }));
      for (const tag of tags) {
        const key = tag.sourceId + '|' + tag.itemId;
        const listed = held.has(key);
        const row = h('label', { class: 'pick-row' + (listed ? ' listed' : '') },
          h('input', {
            type: 'checkbox', checked: selected.has(key) || listed, disabled: listed,
            onchange: (e) => { e.target.checked ? selected.add(key) : selected.delete(key); addButton.disabled = !selected.size; addButton.textContent = selected.size ? 'Add selected (' + selected.size + ')' : 'Add selected'; }
          }),
          h('span', { class: 'pick-name', text: tag.displayName || tag.itemId }),
          h('span', { class: 'pick-id mono', text: tag.itemId }),
          tag.unit && tag.unit.trim() ? h('span', { class: 'pick-unit', text: tag.unit }) : null,
          listed ? h('span', { class: 'pick-unit', text: "in the area's tags" }) : null);
        list.appendChild(row);
      }
    }

    if (!list.children.length) {
      list.appendChild(h('p', { class: 'muted small', text: catalogSources.length
        ? 'No tags match "' + (search.value || '').trim() + '".'
        : 'No mapped tags on this bridge — map them in the dashboard (Tags ▸ Maps), then reopen this window.' }));
    }
  };

  const search = h('input', {
    type: 'search', class: 'pick-search', placeholder: 'search the bridge\'s mapped tags', 'aria-label': 'Search tags',
    oninput: paint
  });

  const close = () => { overlay.remove(); document.removeEventListener('keydown', onKey); };
  const onKey = (e) => { if (e.key === 'Escape') close(); };

  addButton.addEventListener('click', () => {
    const taken = new Set(tagDraft.tags.map((t) => (t.name || '').trim().toLowerCase()));
    let added = 0;
    for (const tag of catalog) {
      if (!selected.has(tag.sourceId + '|' + tag.itemId)) continue;
      tagDraft.tags.push({ name: tagNameFor(tag.displayName, tag.itemId, taken), sourceId: tag.sourceId, itemId: tag.itemId });
      added += 1;
    }

    close();
    renderEditor();
    toast((added === 1 ? '1 tag' : added + ' tags') + ' added to "' + tagArea + '" — Save to store them.', 'ok');
  });

  overlay.appendChild(h('div', { class: 'sheet', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Add tags from the bridge' },
    h('div', { class: 'sheet-head' },
      h('span', { class: 'res-name', text: 'Bridge tags' }),
      h('span', { class: 'muted small', text: (catalog || []).length + ' mapped' }),
      h('span', { class: 'grow' }),
      h('button', { class: 'btn', type: 'button', onclick: close }, 'Cancel')),
    search,
    list,
    h('div', { class: 'sheet-foot' },
      // The HMI's own note: the readings are a snapshot, not a live feed.
      h('span', { class: 'muted small', text: 'Values are as read when this window opened. Nothing is saved until Save.' }),
      h('span', { class: 'grow' }),
      addButton)));

  overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
  document.addEventListener('keydown', onKey);
  document.body.appendChild(overlay);
  search.focus();
  paint();
}

// ---------------------------------------------------------------- commands

function selectBlock(id) {
  const block = defs.blocks.find((b) => b.id === id);
  if (!block) return;
  // Never drop unsaved edits on a block switch, and never with a blocking dialog: say what to do
  // instead (a page frozen on confirm() would also stall a WebView host).
  if (dirty && draft && draft.id !== id) {
    setSaveMessage('Unsaved changes — Save or Revert before opening another block.', 'warn');
    toast('Save or revert "' + draft.name + '" first.', 'err');
    return;
  }

  draft = clone(block);
  draftForm = formOf(block);
  tab = 'editor';
  tagArea = '';
  tagDraft = null;
  newTagForId = '';
  dirty = false;
  renderRail();
  renderEditor();
  renderResult();
}

function saveBlock() {
  if (!draft) return;
  saveDraft(false);
}

function saveDraft(quiet) {
  fetchJson(api.save, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ block: draft }) })
    .then((body) => {
      dirty = false;
      version = body.version || version;
      const saved = body.block;
      const index = defs.blocks.findIndex((b) => b.id === saved.id);
      if (index >= 0) defs.blocks[index] = saved; else defs.blocks.push(saved);
      draft = clone(saved);
      draftForm = formOf(saved);
      newTagForId = '';
      renderRail();
      renderEditor();
      if (!quiet) setSaveMessage('Saved · v' + version, 'ok');
      toast('Saved "' + saved.name + '"', 'ok');
    })
    .catch((error) => setSaveMessage(error.message, 'err'));
}

function deleteBlock(block) {
  fetchJson(api.del(block.id), { method: 'DELETE' })
    .then(() => {
      defs.blocks = defs.blocks.filter((b) => b.id !== block.id);
      draft = null;
      dirty = false;
      renderRail();
      renderEditor();
      renderResult();
      toast('Deleted "' + block.name + '"', 'ok');
    })
    .catch((error) => toast(error.message, 'err'));
}

function newBlockForm() {
  if (dirty) {
    setSaveMessage('Unsaved changes — Save or Revert before starting another block.', 'warn');
    toast('Save or revert "' + draft.name + '" first.', 'err');
    return;
  }

  draft = null;
  dirty = false;
  renderRail();
  const editor = el('editor');
  editor.textContent = '';
  const groups = Array.from(new Set(defs.blocks.map((b) => (b.group || '').trim()).filter((g) => g)));
  const groupList = h('datalist', { id: 'groupNames' }, groups.map((g) => h('option', { value: g })));
  const areaNames = Array.from(new Set([
    ...(defs.tagSources || []).map((a) => (a.name || '').trim()),
    ...defs.blocks.map((b) => (b.area || '').trim())
  ].filter((name) => name)));
  const areaList = h('datalist', { id: 'areaNamesNew' }, areaNames.map((name) => h('option', { value: name })));

  editor.appendChild(h('div', { class: 'ed-head' },
    h('div', { class: 'section-title', text: 'New block' }),
    h('div', { class: 'field' },
      h('label', { for: 'nbName', text: 'Name' }),
      h('input', { type: 'text', id: 'nbName', maxlength: 64, placeholder: 'e.g. Primary Arm Up' })
    ),
    h('div', { class: 'field' },
      h('label', { for: 'nbKind', text: 'Kind' }),
      h('select', { id: 'nbKind' }, BLOCK_KINDS.map((k) => h('option', { value: k, text: k })))
    ),
    h('div', { class: 'field' },
      h('label', { for: 'nbGroup', text: 'Group' }),
      h('input', { type: 'text', id: 'nbGroup', list: 'groupNames', maxlength: 40, placeholder: 'optional heading, e.g. Primary Arm' })
    ),
    h('div', { class: 'field' },
      h('label', { for: 'nbArea', text: 'Area' }),
      h('input', { type: 'text', id: 'nbArea', list: 'areaNamesNew', maxlength: 40, placeholder: 'e.g. Dry End' })
    ),
    groupList,
    areaList,
    h('div', { class: 'savebar' },
      h('span', { class: 'msg', text: 'A new block starts with one contact — give it a sentence and a tag name on the Editor tab (its source and tag are assigned on the Tags tab), and the bridge refuses a block with no wired condition.' }),
      h('span', { class: 'grow' }),
      h('button', { class: 'btn', type: 'button', onclick: () => { renderEditor(); } }, 'Cancel'),
      h('button', {
        class: 'btn primary', type: 'button', onclick: () => {
          const name = val('nbName').trim();
          if (!name) { toast('A block needs a name.', 'err'); return; }
          draft = newBlock(name, val('nbKind'), val('nbArea').trim(), val('nbGroup').trim());
          draftForm = 'network';
          tab = 'editor';
          tagArea = '';
          tagDraft = null;
          newTagForId = '';
          dirty = true;
          renderEditor();
        }
      }, 'Create')
    )
  ));
}

const val = (id) => (el(id) ? el(id).value : '');

// ---------------------------------------------------------------- live state

function applyState(snapshot) {
  elementStates = new Map();
  blockStates = new Map();
  for (const block of snapshot.blocks || []) {
    blockStates.set(block.id, block);
    for (const element of block.elements || []) elementStates.set(element.id, element);
  }

  for (const row of document.querySelectorAll('.el[data-live]')) {
    const state = elementStates.get(row.dataset.id);
    const box = row.querySelector('.el-live');
    if (!box) continue;
    if (state) paintLive(box, state.kind, state);
    else {
      const q = box.querySelector('.q');
      q.className = 'q unknown';
      q.textContent = '—';
      box.querySelector('.reading').textContent = 'not in this evaluation';
    }
  }

  for (const row of document.querySelectorAll('.block-row')) {
    const state = blockStates.get(row.dataset.block);
    const dot = row.querySelector('.state-dot');
    dot.className = 'state-dot ' + (state ? state.state : '');
    const block = defs.blocks.find((b) => b.id === row.dataset.block);
    const why = row.querySelector('.block-why');
    if (why && block) why.textContent = blockWhy(block, state);
    row.title = state && state.reason ? state.state + ' — ' + state.reason : (state ? state.state : '');
  }

  renderResult();

  lastEvaluated = snapshot.evaluatedUtc ? new Date(snapshot.evaluatedUtc) : lastEvaluated;
  if (lastEvaluated) el('evaluated').textContent = 'evaluated ' + formatTime(lastEvaluated);

  // The Result tab's cards and the Tags tab's live column are painted in place, like the
  // editor's readings: a poll must never rewrite the tree the operator is reading.
  for (const card of document.querySelectorAll('.rl-card')) {
    const row = card.parentElement;
    const state = elementStates.get(row.dataset.id);
    if (state) paintResultCard(card, state);
  }

  const blockState = draft ? blockStates.get(draft.id) : null;
  for (const pill of document.querySelectorAll('[data-step]')) {
    const step = ((blockState && blockState.steps) || []).find((s) => s.id === pill.dataset.step);
    const word = step ? step.state : 'pending';
    pill.textContent = STEP_WORD[word] || word;
    pill.className = 'pill step-pill ' + (word === 'done' ? 'ok' : word === 'current' ? 'warn' : 'muted');
  }

  for (const cell of document.querySelectorAll('[data-live-tag]')) paintTagLive(cell);
  refreshResultHeader();

  // The Tags tab's live column comes from the bridge's tag snapshot rather than the logic state,
  // so it is refreshed on a slower cadence while that tab is open.
  if (tab === 'tags' && catalog && Date.now() - catalogFetchedAt > 5000) {
    loadCatalog().then(() => { for (const cell of document.querySelectorAll('[data-live-tag]')) paintTagLive(cell); });
  }

  // Another author (the HMI) saved while this page was open.
  if (snapshot.version && snapshot.version !== version) {
    setSaveMessage('The bridge holds newer definitions (v' + snapshot.version + '). Save to overwrite, or reload the page.', 'warn');
  }
}

// One Result-tab card, repainted from the new evaluation without touching its structure.
function paintResultCard(card, state) {
  const def = card.__def || null;
  card.className = 'rl-card ' + statePill(state.state);

  const mark = card.querySelector('.rl-mark');
  if (mark) mark.textContent = state.state === 'true' ? '✓' : state.state === 'false' ? '✗' : '—';

  const actual = card.querySelector('.rl-actual');
  if (actual) actual.textContent = actualBit(state, def);

  const value = card.querySelector('.rl-value');
  if (value) value.textContent = state.valueText || '—';

  const should = card.querySelector('.rl-should');
  if (should) {
    should.className = 'rl-line rl-should ' + (state.matches === true ? 'ok' : state.matches === false ? 'bad' : 'unknown');
    should.textContent = shouldLine(state, def);
  }

  const progress = card.querySelector('.rl-progress');
  if (progress) progress.textContent = progressLine(state, def);
}

function paintTagLive(cell) {
  const parts = (cell.dataset.liveTag || '').split('|');
  cell.textContent = parts[0] && parts[1] ? liveText(catalogTag(parts[0], parts[1])) : '—';
}

// The tag lines of the rendered result rows, re-labelled once the bridge's names arrive.
function repaintResultTags() {
  for (const card of document.querySelectorAll('.rl-card')) {
    const label = card.querySelector('.rl-tag');
    if (!label || !card.__def) continue;
    const text = tagLabel(card.__def);
    if (text) label.textContent = text;
  }

  for (const cell of document.querySelectorAll('[data-live-tag]')) paintTagLive(cell);
}

function poll() {
  if (document.hidden) return;
  fetchJson(api.state)
    .then((snapshot) => {
      lastPollOk = Date.now();
      feedState = 'live';
      const pill = el('linkState');
      pill.className = 'pill live';
      pill.textContent = 'live';
      applyState(snapshot);
      if (snapshot.version && snapshot.version !== version) return reloadDefs(false);
      return null;
    })
    .catch((error) => {
      const recent = Date.now() - lastPollOk < 5000;
      feedState = recent ? 'reconnecting' : 'failed';
      const pill = el('linkState');
      pill.className = 'pill ' + (recent ? 'stale' : 'down');
      pill.textContent = recent ? 'reconnecting…' : 'no link';
      if (error && /Sign in/.test(error.message)) pill.textContent = 'sign in required';
      refreshResultHeader();
    });
}

// Theme ---------------------------------------------------------------------
//
// The dashboard's engine under the dashboard's own key: the choice is a browser
// preference, not a bridge setting, so one operator picking dark cannot change the
// plant's screens — and because the key is shared, this page and the dashboard open
// in the same theme. <html data-theme> is the single switch (every colour reads back
// from the CSS tokens), and index.html resolves it before first paint.

const THEME_KEY = 'opcbridge.theme';
const THEME_PREFS = ['light', 'dark', 'system'];
const systemTheme = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

function storedThemePref() {
  try {
    const stored = localStorage.getItem(THEME_KEY);
    if (THEME_PREFS.indexOf(stored) >= 0) return stored;
  } catch (e) { /* private mode */ }
  return 'system';
}

function resolvedTheme(pref) {
  if (pref !== 'system') return pref;
  return systemTheme && systemTheme.matches ? 'dark' : 'light';
}

let themePref = storedThemePref();

function applyTheme(pref, persist) {
  themePref = THEME_PREFS.indexOf(pref) >= 0 ? pref : 'system';
  document.documentElement.setAttribute('data-theme', resolvedTheme(themePref));
  if (persist) { try { localStorage.setItem(THEME_KEY, themePref); } catch (e) { /* private mode */ } }
  for (const input of document.querySelectorAll('input[name="theme"]')) input.checked = input.value === themePref;
}

function initTheme() {
  applyTheme(themePref, false);
  for (const input of document.querySelectorAll('input[name="theme"]')) {
    input.addEventListener('change', () => { if (input.checked) applyTheme(input.value, true); });
  }

  if (!systemTheme) return;
  const followSystem = () => { if (themePref === 'system') applyTheme('system', false); };
  if (systemTheme.addEventListener) systemTheme.addEventListener('change', followSystem);
  else if (systemTheme.addListener) systemTheme.addListener(followSystem);
}

function start() {
  el('btnNew').addEventListener('click', newBlockForm);
  initTheme();
  // The network's rows hold the top of the editor while their own children are on screen, and which
  // of them is holding it is read from the scroll — see syncNetPinning.
  el('editor').addEventListener('scroll', schedulePinnedRows, { passive: true });
  // The rows pin under the head, whose height follows the window (a narrower one wraps its note):
  // the offset the network cleared at the last render is re-measured when the window changes.
  window.addEventListener('resize', syncNetPinning);
  document.addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      if (dirty) saveBlock();
    }
  });

  reloadDefs(false).then(() => { poll(); setInterval(poll, POLL_MS); })
    .catch((error) => toast(error.message, 'err'));
}

document.addEventListener('DOMContentLoaded', start);
