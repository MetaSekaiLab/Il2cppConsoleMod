'use strict';
/* Il2Cpp Console — web inspector UI. Plain JS, no build step, served from the mod DLL. */

// ============================================================================================
// utilities
// ============================================================================================

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k === 'html') el.innerHTML = v;
      else if (k === 'style' && typeof v === 'object') {
        for (const [sk, sv] of Object.entries(v)) {
          if (sk.startsWith('--')) el.style.setProperty(sk, sv);
          else el.style[sk] = sv;
        }
      } else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (v === true) el.setAttribute(k, '');
      else el.setAttribute(k, v);
    }
  }
  return append(el, kids);
}

function append(el, kids) {
  for (const k of kids.flat(Infinity)) {
    if (k == null || k === false) continue;
    el.append(k instanceof Node ? k : String(k));
  }
  return el;
}

function icon(name, cls = 'i') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', cls);
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', '#i-' + name);
  svg.append(use);
  return svg;
}

function iconBtn(name, title, onclick, cls = 'icon-btn sm') {
  return h('button', { class: cls, type: 'button', title, onclick: e => { e.stopPropagation(); onclick(e); } }, icon(name));
}

function debounce(fn, ms) {
  let t = 0;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
}

function storage(kind) {
  try { return kind === 'session' ? window.sessionStorage : window.localStorage; } catch { return null; }
}

const store = {
  get(key, fallback = null, kind = 'local') {
    try {
      const raw = storage(kind)?.getItem('ilc.' + key);
      return raw == null ? fallback : JSON.parse(raw);
    } catch { return fallback; }
  },
  set(key, value, kind = 'local') {
    try { storage(kind)?.setItem('ilc.' + key, JSON.stringify(value)); } catch { /* storage unavailable */ }
  },
  del(key, kind = 'local') {
    try { storage(kind)?.removeItem('ilc.' + key); } catch { /* storage unavailable */ }
  },
};

function toast(message, kind = '') {
  const el = h('div', { class: 'toast ' + kind, text: message });
  $('#toasts').append(el);
  setTimeout(() => el.remove(), kind === 'err' ? 6500 : 2800);
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast('已复制', 'ok');
  } catch {
    const area = h('textarea', { style: { position: 'fixed', opacity: '0' } });
    area.value = text;
    document.body.append(area);
    area.select();
    document.execCommand('copy');
    area.remove();
    toast('已复制', 'ok');
  }
}

const lastSegment = s => (s || '').split('.').pop();
const fmtMb = mb => (mb >= 1024 ? (mb / 1024).toFixed(1) + ' GB' : Math.round(mb) + ' MB');

// ============================================================================================
// API client
// ============================================================================================

class ApiError extends Error {
  constructor(status, message, code) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

const API = {
  token: '',
  async call(path, { method = 'GET', params, body, blob = false, text = false } = {}) {
    let url = path;
    if (params) {
      const q = new URLSearchParams();
      for (const [k, v] of Object.entries(params)) if (v != null) q.set(k, v);
      url += '?' + q;
    }
    const headers = { 'X-Debug-Token': this.token };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    let res;
    try {
      res = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store' });
    } catch (e) {
      throw new ApiError(0, '无法连接到游戏', 'offline');
    }
    if (res.status === 401) {
      Auth.unauthorized();
      throw new ApiError(401, 'token 无效', 'unauthorized');
    }
    if (!res.ok) {
      let data = {};
      try { data = await res.json(); } catch { /* not json */ }
      throw new ApiError(res.status, data.error || res.statusText, data.code);
    }
    if (blob) return res.blob();
    if (text) return res.text();
    return res.json();
  },
  get(path, params) { return this.call(path, { params }); },
  post(path, body) { return this.call(path, { method: 'POST', body: body ?? {} }); },
};

function describeError(e) {
  if (e?.code === 'main-thread-timeout') return '游戏主线程没有响应：可能正在加载，或游戏在后台被暂停（可在游戏设置中开启“后台运行”）';
  if (e?.code === 'expired') return '对象句柄已过期，请重新获取';
  if (e?.code === 'destroyed') return '对象已被销毁（可能切换了场景）';
  if (e?.code === 'offline') return '无法连接到游戏';
  return e?.message || String(e);
}

function fail(e) {
  if (e?.status !== 401) toast(describeError(e), 'err');
}

// ============================================================================================
// values
// ============================================================================================

function swatch(c) {
  const [r, g, b, a] = c.map(x => Math.max(0, Math.min(1, x)));
  return h('span', { class: 'swatch' }, h('i', { style: { background: `rgba(${r * 255 | 0}, ${g * 255 | 0}, ${b * 255 | 0}, ${a})` } }));
}

function canExpand(v) {
  return !!(v && v.h && v.k !== 'str' && v.k !== 'destroyed');
}

/** Inline rendering of a value DTO: {k, t, v, h, n, c, trunc}. */
function renderValue(v, { link = true } = {}) {
  v = v || { k: 'null' };
  const el = h('span', { class: 'val k-' + v.k });
  switch (v.k) {
    case 'null': el.textContent = 'null'; break;
    case 'void': el.textContent = '无返回值'; break;
    case 'str': {
      const body = JSON.stringify(v.v ?? '');
      el.textContent = v.trunc ? body.slice(0, -1) + '…"' : body;
      el.title = `string · ${v.n ?? (v.v || '').length} 字符` + (v.trunc ? ' · 点击查看全文' : '');
      if (v.trunc && v.h) {
        el.classList.add('link');
        el.addEventListener('click', e => { e.stopPropagation(); showText(v.h); });
      }
      return el;
    }
    case 'char': el.textContent = `'${v.v}'`; break;
    case 'num': case 'bool': case 'ptr': el.textContent = v.v; el.title = v.t || ''; break;
    case 'enum': append(el, [h('span', { class: 'ep', text: lastSegment(v.t) + '.' }), v.v]); el.title = v.t; break;
    case 'type': append(el, [h('span', { class: 'n', text: 'type ' }), h('span', { class: 't', text: v.v })]); break;
    case 'destroyed': append(el, [h('span', { class: 't', text: v.t }), ' destroyed']); break;
    case 'err': el.textContent = '⚠ ' + (v.v || 'error'); el.title = v.v || ''; break;
    case 'struct':
      if (v.c) el.append(swatch(v.c));
      el.append(h('span', { class: 't', text: v.t }));
      if (v.v) el.append(' ', h('span', { class: 'p', text: v.v }));
      break;
    default:
      el.append(h('span', { class: 't', text: v.t }));
      if (v.v != null && v.v !== '') el.append(' ', h('span', { class: 'p', text: '“' + v.v + '”' }));
      if (v.n != null) el.append(' ', h('span', { class: 'n', text: `(${v.n})` }));
  }
  if (link && v.h) {
    el.classList.add('link');
    el.title = `检查 $${v.h}`;
    el.addEventListener('click', e => { e.stopPropagation(); Inspector.open(v.h); });
  }
  return el;
}

function valueText(v) {
  if (!v) return 'null';
  switch (v.k) {
    case 'null': return 'null';
    case 'str': return v.v ?? '';
    case 'char': return v.v;
    case 'enum': return v.v;
    case 'type': return v.v;
    case 'struct': return v.v ?? v.t;
    case 'obj': return v.v ? `${v.t} ${v.v}` : v.t;
    default: return v.v ?? v.k;
  }
}

async function showText(handle) {
  try {
    const data = await API.get('/api/text', { h: handle });
    Modal.open(`字符串 · ${data.text.length} 字符`, h('pre', { text: data.text }), [
      h('button', { class: 'btn small', type: 'button', onclick: () => copyText(data.text) }, icon('copy'), '复制'),
    ]);
  } catch (e) { fail(e); }
}

// ============================================================================================
// value tree: expandable members / items, used by inspector, console, watches, variables
// ============================================================================================

class VTree {
  constructor({ compact = false } = {}) {
    this.el = h('div', { class: 'vtree' + (compact ? ' compact' : '') });
    this.compact = compact;
    this.expanded = new Set();
    this.collapsedGroups = new Set();
    this.values = new Map();
    this.fresh = new Map();
  }

  /** Call after a full rebuild so the next rebuild can flash what changed. */
  commit() {
    this.values = this.fresh;
    this.fresh = new Map();
  }

  reset() {
    this.expanded.clear();
    this.values.clear();
    this.fresh.clear();
  }

  track(path, row, sig) {
    if (this.values.has(path) && this.values.get(path) !== sig) row.classList.add('flash');
    this.fresh.set(path, sig);
  }

  async members(members, depth, ctx, path, { grouped = false, filter = null } = {}) {
    const frag = document.createDocumentFragment();
    let decl = null;
    let body = frag;
    for (const m of members) {
      if (filter && !filter(m)) continue;
      if (grouped && m.decl !== decl) {
        decl = m.decl;
        const key = decl;
        const open = !this.collapsedGroups.has(key);
        const count = members.filter(x => x.decl === decl && (!filter || filter(x))).length;
        const head = h('div', { class: 'v-group' + (open ? ' open' : ''), style: { '--depth': depth } },
          icon('chev'), h('span', { text: decl }), h('span', { class: 'ct', text: String(count) }), h('span', { class: 'ln' }));
        const groupBody = h('div', { hidden: !open });
        head.addEventListener('click', () => {
          const nowOpen = groupBody.hidden;
          groupBody.hidden = !nowOpen;
          head.classList.toggle('open', nowOpen);
          if (nowOpen) this.collapsedGroups.delete(key); else this.collapsedGroups.add(key);
        });
        frag.append(head, groupBody);
        body = groupBody;
      }
      body.append(await this.memberNode(m, depth, ctx, path + '/' + m.name));
    }
    return frag;
  }

  async memberNode(m, depth, ctx, path) {
    const v = m.value;
    const expandable = canExpand(v) && !m.error;
    const node = h('div', { class: 'v-node' });
    const kids = h('div', { class: 'v-kids' });
    const caret = h('span', { class: 'caret' + (expandable ? '' : ' empty') }, icon('chev'));
    const markerClass = m.kind === 'prop' ? 'mk prop' : m.kind === 'const' ? 'mk const' : 'mk';
    const marker = h('span', {
      class: markerClass,
      style: m.kind === 'field' && !m.native ? { background: 'var(--fg-3)' } : null,
      title: m.native ? 'Il2Cpp 字段' : m.kind === 'prop' ? '属性' : m.kind === 'const' ? '常量' : '托管字段',
    });
    const nameCell = h('div', { class: 'v-name' }, caret, marker,
      m.static && !ctx.staticView ? h('span', { class: 'st', text: 'S', title: 'static' }) : null,
      h('span', { class: 'nm ' + (m.kind === 'prop' ? 'prop' : m.kind === 'const' ? 'const' : ''), text: m.name, title: `${m.name} · ${m.decl}` }));
    const typeCell = h('div', { class: 'v-type', text: m.type, title: m.type });
    const row = h('div', { class: 'v-row', style: { '--depth': depth } }, nameCell, typeCell, this.valueCell(m, ctx, node, depth, path));
    this.track(path, row, m.error ? 'e:' + m.error : valueSig(v));

    if (expandable) {
      nameCell.addEventListener('click', e => { if (!e.target.closest('.link')) this.toggle(row, kids, v, depth, path); });
      row.addEventListener('dblclick', e => { if (!e.target.closest('input,select,button,.v-name')) this.toggle(row, kids, v, depth, path); });
    }
    row.addEventListener('contextmenu', e => valueMenu(e, v, m.name));
    node.append(row, kids);
    if (expandable && this.expanded.has(path)) {
      row.classList.add('open');
      await this.fill(kids, v, depth, path);
    }
    return node;
  }

  valueCell(m, ctx, node, depth, path) {
    const cell = h('div', { class: 'v-val' });
    const v = m.value;
    if (m.error) {
      cell.append(h('span', { class: 'val k-err', text: '⚠ ' + m.error, title: m.error }));
      return cell;
    }
    if (m.lazy) {
      cell.append(h('button', {
        class: 'link-btn', type: 'button',
        text: m.lazyHint ? '读取（有副作用）' : '读取',
        title: m.lazyHint ? '这个属性的 getter 会改变状态（创建材质 / 网格副本、推进随机数等），只在点击时读取' : '静态属性默认不自动读取：单例的 Instance 等 getter 可能会顺带创建对象',
        onclick: async () => {
          try {
            const fresh = await API.get('/api/member', { h: ctx.h, name: m.name });
            node.replaceWith(await this.memberNode(fresh, depth, ctx, path));
          } catch (e) { fail(e); }
        },
      }));
      return cell;
    }

    const editable = m.canWrite && m.edit && ctx.h != null;
    if (editable && m.edit === 'bool') {
      const box = h('input', { type: 'checkbox', class: 'bool-toggle', title: '切换' });
      box.checked = v?.v === 'true';
      box.addEventListener('change', () => this.commitEdit(m, ctx, node, depth, path, box.checked ? 'true' : 'false'));
      cell.append(box);
    }
    cell.append(renderValue(v));

    const acts = h('div', { class: 'v-acts' });
    if (editable && m.edit !== 'bool') {
      const pencil = iconBtn('edit', '编辑（双击值也可以）', () => this.startEdit(m, ctx, node, depth, path, cell));
      pencil.classList.add('edit-btn');
      acts.append(pencil);
      cell.addEventListener('dblclick', e => { e.stopPropagation(); this.startEdit(m, ctx, node, depth, path, cell); });
    }
    if (v?.h) {
      acts.append(iconBtn('dollar', `在控制台引用 $${v.h}`, () => Console.insert('$' + v.h)));
      acts.append(iconBtn('right', '打开', () => Inspector.open(v.h)));
    }
    acts.append(iconBtn('copy', '复制值', () => copyText(valueText(v))));
    cell.append(acts);
    return cell;
  }

  startEdit(m, ctx, node, depth, path, cell) {
    if (cell.querySelector('.v-edit')) return;
    const v = m.value || {};
    const wrap = h('div', { class: 'v-edit' });
    let input;
    if (m.edit === 'enum') {
      input = h('select', {}, (m.options || []).map(o => h('option', { value: o, text: o })));
      input.value = v.v;
    } else {
      input = h('input', { type: 'text', spellcheck: 'false' });
      input.value = editText(m, v);
      if (m.edit === 'vec' && m.options) input.placeholder = m.options.join(', ');
      if (m.edit === 'color') {
        const picker = h('input', { type: 'color', title: '取色' });
        picker.value = (input.value.startsWith('#') ? input.value : '#ffffff').slice(0, 7);
        picker.addEventListener('input', () => { input.value = picker.value + (input.value.length === 9 ? input.value.slice(7) : 'ff'); });
        wrap.append(picker);
      }
    }
    wrap.prepend(input);
    const restore = () => { wrap.remove(); Array.from(cell.children).forEach(c => { c.hidden = false; }); Inspector.editing = false; };
    Array.from(cell.children).forEach(c => { c.hidden = true; });
    cell.append(wrap);
    Inspector.editing = true;
    input.focus();
    input.select?.();
    const commit = () => { restore(); this.commitEdit(m, ctx, node, depth, path, input.value); };
    input.addEventListener('keydown', e => {
      if (e.key === 'Enter') { e.preventDefault(); commit(); }
      if (e.key === 'Escape') { e.preventDefault(); restore(); }
    });
    if (m.edit === 'enum') input.addEventListener('change', commit);
    input.addEventListener('blur', () => setTimeout(() => { if (wrap.isConnected && !wrap.contains(document.activeElement)) restore(); }, 120));
  }

  async commitEdit(m, ctx, node, depth, path, text) {
    try {
      const fresh = await API.post('/api/set', { h: ctx.h, name: m.name, value: text });
      node.replaceWith(await this.memberNode(fresh, depth, ctx, path));
      toast(`${m.name} 已更新`, 'ok');
    } catch (e) {
      fail(e);
    }
  }

  async toggle(row, kids, v, depth, path) {
    if (row.classList.contains('open')) {
      row.classList.remove('open');
      kids.replaceChildren();
      this.expanded.delete(path);
      return;
    }
    row.classList.add('open');
    this.expanded.add(path);
    await this.fill(kids, v, depth, path);
  }

  async fill(kids, v, depth, path) {
    kids.replaceChildren(h('div', { class: 'loading-row', style: { '--depth': depth + 1 } }, h('span', { class: 'spin' }), ' 加载中'));
    try {
      const data = await API.get('/api/inspect', { h: v.h });
      kids.replaceChildren(await this.children(data, depth + 1, path));
    } catch (e) {
      kids.replaceChildren(msgRow(describeError(e), depth + 1, true));
    }
  }

  /** Children of an inspected object: items first for collections, then members. */
  async children(data, depth, path) {
    const frag = document.createDocumentFragment();
    const ctx = { h: data.h, staticView: data.isStatic };
    if (data.facets?.includes('items')) {
      try {
        const page = await API.get('/api/items', { h: data.h, offset: 0, limit: 100 });
        frag.append(await this.items(page, depth, ctx, path, data.h));
      } catch (e) {
        frag.append(msgRow(describeError(e), depth, true));
      }
      if (data.members.length) frag.append(await this.lazyGroup('成员', data.members.length, depth, path + '/#members',
        () => this.members(data.members, depth, ctx, path)));
    } else if (data.members.length) {
      frag.append(await this.members(data.members, depth, ctx, path));
    } else {
      frag.append(msgRow('没有公开成员', depth));
    }
    return frag;
  }

  async lazyGroup(label, count, depth, key, build) {
    const wrap = h('div');
    const open = this.expanded.has(key);
    const head = h('div', { class: 'v-group' + (open ? ' open' : ''), style: { '--depth': depth } },
      icon('chev'), h('span', { text: label }), h('span', { class: 'ct', text: String(count) }), h('span', { class: 'ln' }));
    const body = h('div');
    head.addEventListener('click', async () => {
      if (head.classList.toggle('open')) {
        this.expanded.add(key);
        body.replaceChildren(await build());
      } else {
        this.expanded.delete(key);
        body.replaceChildren();
      }
    });
    if (open) body.append(await build());
    wrap.append(head, body);
    return wrap;
  }

  async items(page, depth, ctx, path, handle) {
    const frag = document.createDocumentFragment();
    for (const it of page.items) frag.append(await this.itemNode(it, depth, path));
    if (!page.items.length && page.offset === 0) frag.append(msgRow('空集合', depth));
    if (page.more) {
      const next = page.offset + page.items.length;
      const more = h('div', { class: 'v-more', style: { '--depth': depth } },
        h('button', {
          class: 'btn small', type: 'button',
          text: page.count != null ? `再加载 100 项（${next} / ${page.count}）` : '再加载 100 项',
          onclick: async () => {
            more.replaceChildren(h('span', { class: 'spin' }));
            try {
              const nextPage = await API.get('/api/items', { h: handle, offset: next, limit: 100 });
              more.replaceWith(await this.items(nextPage, depth, ctx, path, handle));
            } catch (e) { fail(e); }
          },
        }));
      frag.append(more);
    }
    return frag;
  }

  async itemNode(it, depth, path) {
    const v = it.value;
    const p = path + '/[' + it.i + ']';
    const expandable = canExpand(v);
    const node = h('div', { class: 'v-node' });
    const kids = h('div', { class: 'v-kids' });
    const caret = h('span', { class: 'caret' + (expandable ? '' : ' empty') }, icon('chev'));
    const label = it.key
      ? h('span', { class: 'nm' }, h('span', { class: 'n', text: '[' }), renderValue(it.key), h('span', { class: 'n', text: ']' }))
      : h('span', { class: 'nm idx', text: `[${it.i}]` });
    const nameCell = h('div', { class: 'v-name' }, caret, label);
    const row = h('div', { class: 'v-row', style: { '--depth': depth } },
      nameCell,
      h('div', { class: 'v-type', text: v?.t || '', title: v?.t || '' }),
      this.valueCell({ name: `[${it.i}]`, value: v }, {}, node, depth, p));
    this.track(p, row, valueSig(v) + '|' + valueSig(it.key));
    if (expandable) {
      nameCell.addEventListener('click', e => { if (!e.target.closest('.link')) this.toggle(row, kids, v, depth, p); });
      row.addEventListener('dblclick', e => { if (!e.target.closest('input,select,button,.v-name')) this.toggle(row, kids, v, depth, p); });
    }
    row.addEventListener('contextmenu', e => valueMenu(e, v, `[${it.i}]`));
    node.append(row, kids);
    if (expandable && this.expanded.has(p)) {
      row.classList.add('open');
      await this.fill(kids, v, depth, p);
    }
    return node;
  }

  /** A single value as an expandable root row (console results, watches). */
  async root(v, path = '$') {
    const node = h('div', { class: 'v-node' });
    const kids = h('div', { class: 'v-kids' });
    const expandable = canExpand(v);
    const caret = h('span', { class: 'caret' + (expandable ? '' : ' empty') }, icon('chev'));
    const acts = h('div', { class: 'v-acts' });
    if (v?.h) {
      acts.append(iconBtn('dollar', `在控制台引用 $${v.h}`, () => Console.insert('$' + v.h)));
      acts.append(iconBtn('right', '打开', () => Inspector.open(v.h)));
      acts.append(iconBtn('pin', '收藏', () => Session.pin(v)));
    }
    acts.append(iconBtn('copy', '复制值', () => copyText(valueText(v))));
    const row = h('div', { class: 'v-row', style: { '--depth': 0 } },
      h('div', { class: 'v-name' }, caret, renderValue(v), v?.h ? h('span', { class: 'n', style: { marginLeft: '8px', color: 'var(--fg-4)' }, text: '$' + v.h }) : null),
      h('div', { class: 'v-val' }, acts));
    if (expandable) {
      caret.addEventListener('click', () => this.toggle(row, kids, v, 0, path));
      row.addEventListener('dblclick', () => this.toggle(row, kids, v, 0, path));
    }
    row.addEventListener('contextmenu', e => valueMenu(e, v));
    node.append(row, kids);
    if (expandable && this.expanded.has(path)) {
      row.classList.add('open');
      await this.fill(kids, v, 0, path);
    }
    return node;
  }
}

/** What a value looks like, ignoring its handle (struct values get a fresh handle on every read). */
function valueSig(v) {
  return v ? [v.k, v.t, v.v, v.n, v.c].join('\u0001') : '';
}

function editText(m, v) {
  if (m.edit === 'str') return v.k === 'null' ? '' : (v.v ?? '');
  if (m.edit === 'color' && v.c) {
    const hex = x => Math.round(Math.max(0, Math.min(1, x)) * 255).toString(16).padStart(2, '0');
    return '#' + v.c.map(hex).join('');
  }
  if (m.edit === 'vec' && v.v) return v.v.replace(/^\(|\)$/g, '');
  return v.v ?? '';
}

function msgRow(text, depth, err = false) {
  return h('div', { class: 'v-msg' + (err ? ' err' : ''), style: { '--depth': depth }, text });
}

function valueMenu(e, v, name) {
  if (!v) return;
  e.preventDefault();
  const items = [];
  if (v.h) {
    items.push({ icon: 'right', label: '检查', run: () => Inspector.open(v.h) });
    items.push({ icon: 'dollar', label: `在控制台引用 $${v.h}`, run: () => Console.insert('$' + v.h) });
    items.push({ icon: 'pin', label: '收藏', run: () => Session.pin(v, name && !name.startsWith('[') ? name : undefined) });
    items.push({ icon: 'eye', label: '添加到监视', run: () => Session.addWatch(`$${v.h}`) });
    items.push('-');
  }
  items.push({ icon: 'copy', label: '复制值', run: () => copyText(valueText(v)) });
  if (name) items.push({ icon: 'copy', label: '复制名称', run: () => copyText(name) });
  if (v.t) items.push({ icon: 'copy', label: '复制类型名', run: () => copyText(v.t) });
  Menu.open(e.clientX, e.clientY, items);
}

// ============================================================================================
// menus, modal, palette shells
// ============================================================================================

const Menu = {
  el: null,
  open(x, y, items) {
    const el = $('#menu');
    el.replaceChildren(...items.map(it => it === '-'
      ? h('div', { class: 'menu-sep' })
      : h('button', { class: 'menu-item', type: 'button', onclick: () => { this.close(); it.run(); } },
        it.icon ? icon(it.icon) : null, h('span', { text: it.label }), it.sub ? h('span', { class: 'sub', text: it.sub }) : null)));
    el.hidden = false;
    const rect = el.getBoundingClientRect();
    el.style.left = Math.max(8, Math.min(x, innerWidth - rect.width - 8)) + 'px';
    el.style.top = Math.max(8, Math.min(y, innerHeight - rect.height - 8)) + 'px';
  },
  close() { $('#menu').hidden = true; },
};

const Modal = {
  open(title, body, actions = []) {
    $('#modal-title').replaceChildren(h('span', { text: title }), ...actions.map(a => { a.style.marginLeft = '10px'; return a; }));
    $('#modal-body').replaceChildren(body);
    $('#modal').hidden = false;
  },
  close() { $('#modal').hidden = true; $('#modal-body').replaceChildren(); },
};

// ============================================================================================
// inspector
// ============================================================================================

const Inspector = {
  history: [],
  index: -1,
  data: null,
  tab: 'members',
  tree: new VTree(),
  filter: { text: '', inherited: true, props: true },
  live: false,
  liveTimer: 0,
  editing: false,
  openSeq: 0,
  methodsCache: null,

  init() {
    $('#nav-back').addEventListener('click', () => this.go(this.index - 1));
    $('#nav-forward').addEventListener('click', () => this.go(this.index + 1));
    this.welcome();
  },

  welcome() {
    this.data = null;
    $('#crumbs').replaceChildren(h('span', { class: 'crumb current', text: '开始' }));
    $('#nav-actions').replaceChildren();
    const card = (ic, title, desc, run) => h('button', { class: 'welcome-card', type: 'button', onclick: run }, icon(ic), h('b', { text: title }), h('span', { text: desc }));
    $('#inspector-body').replaceChildren(h('div', { class: 'welcome' },
      h('h2', { text: '检查运行中的游戏' }),
      h('p', { text: '从左侧场景树或类型列表选择对象，像浏览器开发者工具一样逐层展开字段；在下方控制台执行 C#，结果同样可以展开和引用。' }),
      h('div', { class: 'welcome-grid' },
        card('layers', '场景层级', '浏览 GameObject 与组件，开关激活状态', () => Sidebar.show('scene')),
        card('braces', '类型与静态成员', '查找游戏类型，查看单例和静态字段', () => { Sidebar.show('types'); $('#type-filter').focus(); }),
        card('terminal', 'C# 控制台', '会话式执行，$12 直接引用检查中的对象', () => Console.focus()),
        card('search', '命令面板', 'Ctrl+K 搜索类型、对象与命令', () => Palette.open()),
      )));
    this.updateNav();
  },

  async open(handle, { push = true, tab = null } = {}) {
    const seq = ++this.openSeq;
    try {
      const data = await API.get('/api/inspect', { h: handle });
      if (seq !== this.openSeq) return;
      if (data.text != null) { showText(handle); return; }
      this.readAll = false;
      const sameShape = this.data && facetKey(this.data) === facetKey(data);
      if (push) {
        this.history = this.history.slice(0, this.index + 1);
        this.history.push({ h: handle, title: titleOf(data) });
        if (this.history.length > 60) this.history.shift();
        this.index = this.history.length - 1;
      } else if (this.history[this.index]) {
        this.history[this.index].title = titleOf(data);
      }
      this.tree = new VTree();
      this.methodsCache = null;
      this.data = data;
      const tabs = tabsFor(data);
      const wanted = tab || (sameShape ? this.tab : null);
      this.tab = wanted && tabs.includes(wanted) ? wanted : defaultTab(data);
      this.history[this.index].tab = this.tab;
      this.render();
      SceneTree.reveal(data.node?.h);
      Mobile.follow('main');
    } catch (e) {
      if (seq === this.openSeq) fail(e);
    }
  },

  go(index) {
    if (index < 0 || index >= this.history.length) return;
    this.index = index;
    this.open(this.history[index].h, { push: false, tab: this.history[index].tab });
  },

  updateNav() {
    $('#nav-back').disabled = this.index <= 0;
    $('#nav-forward').disabled = this.index >= this.history.length - 1;
  },

  render() {
    const d = this.data;
    this.updateNav();
    this.renderCrumbs();
    this.renderActions();
    const body = $('#inspector-body');
    const scroll = body.scrollTop;
    body.replaceChildren(this.header(d), this.tabStrip(d), h('div', { id: 'obj-pane' }));
    this.renderPane();
    body.scrollTop = scroll;
  },

  renderCrumbs() {
    const crumbs = $('#crumbs');
    crumbs.replaceChildren();
    const start = Math.max(0, this.index - 5);
    if (start > 0) crumbs.append(h('span', { class: 'crumb-sep', text: '…' }));
    for (let i = start; i <= this.index; i++) {
      if (i > start) crumbs.append(h('span', { class: 'crumb-sep' }, icon('chev')));
      const entry = this.history[i];
      crumbs.append(h('button', { class: 'crumb' + (i === this.index ? ' current' : ''), type: 'button', text: entry.title, title: entry.title, onclick: () => this.go(i) }));
    }
  },

  renderActions() {
    const d = this.data;
    const live = h('button', { class: 'icon-btn' + (this.live ? ' on' : ''), type: 'button', title: '实时刷新（每秒）' }, icon('bolt'));
    live.addEventListener('click', () => { this.setLive(!this.live); live.classList.toggle('on', this.live); });
    $('#nav-actions').replaceChildren(
      live,
      iconBtn('refresh', '刷新', () => this.refresh(), 'icon-btn'),
      iconBtn('dollar', `在控制台引用 $${d.h}`, () => Console.insert('$' + d.h), 'icon-btn'),
      iconBtn('pin', '收藏', () => Session.pin(d.value, titleOf(d)), 'icon-btn'),
    );
  },

  setLive(on) {
    this.live = on;
    clearInterval(this.liveTimer);
    if (on) {
      this.liveTimer = setInterval(() => {
        if (!document.hidden && !this.editing && this.data && ['members', 'items', 'components'].includes(this.tab)) this.refresh(true);
      }, 1000);
    }
  },

  async refresh(quiet = false) {
    if (!this.data || this.refreshing) return;
    this.refreshing = true;
    try {
      const data = await API.get('/api/inspect', { h: this.data.h, all: this.readAll ? 1 : null });
      this.data = data;
      if (!quiet) this.methodsCache = null;
      this.updateHeader();
      await this.renderPane(true);
    } catch (e) {
      if (!quiet) fail(e);
      else if (e.code === 'expired' || e.code === 'destroyed') this.setLive(false);
    } finally {
      this.refreshing = false;
    }
  },

  header(d) {
    const node = d.node;
    const isGo = d.facets.includes('gameobject');
    const isComp = d.facets.includes('component');
    const ic = d.isStatic ? 'braces' : isGo ? 'cube' : isComp ? 'package' : d.facets.includes('items') ? 'list' : d.facets.includes('preview') ? 'image' : d.value.k === 'struct' ? 'box' : 'cube';
    const kindLabel = d.isStatic ? (d.type.kind === 'static' ? 'static class' : d.type.kind) : isGo ? 'GameObject' : isComp ? 'Component' : d.type.kind;

    const sub = h('div', { class: 'obj-sub' });
    const typeLink = h('span', { class: 'type-link mono', text: d.type.full, title: d.isStatic ? d.type.full : '查看此类型的静态成员' });
    if (!d.isStatic) typeLink.addEventListener('click', () => openType(d.type.full, d.type.asm));
    sub.append(typeLink, h('span', { class: 'mono', text: d.type.asm }));
    if (d.address) sub.append(h('span', { class: 'mono copyable', text: d.address, title: '复制地址', onclick: () => copyText(d.address) }));
    if (d.count != null) sub.append(h('span', { text: `${d.count} 项` }));
    sub.append(h('span', { class: 'handle', text: '$' + d.h, title: '在控制台引用', onclick: () => Console.insert('$' + d.h) }));
    if (d.type.native) sub.append(h('span', { class: 'pill', title: 'Il2Cpp 原生对象' }, h('span', { class: 'mk', style: { width: '6px', height: '6px', borderRadius: '50%', background: 'var(--native)', display: 'inline-block' } }), 'Il2Cpp'));

    const titleText = titleOf(d);
    const head = h('div', { class: 'obj-head' },
      h('div', { class: 'obj-title-row' },
        h('div', { class: 'obj-icon' }, icon(ic)),
        h('div', { class: 'obj-title' + (d.isStatic ? ' mono' : ''), text: titleText, title: titleText }),
        h('span', { class: 'obj-kind' + (d.isStatic ? ' static' : ''), text: kindLabel })),
      sub);

    if (node) {
      const row = h('div', { class: 'obj-node' });
      if (isGo) {
        const sw = h('label', { class: 'switch', title: 'SetActive' }, h('input', { type: 'checkbox' }), h('span', { class: 'track' }), h('span', { text: node.active ? '激活' : '未激活' }));
        const box = sw.querySelector('input');
        box.checked = node.active;
        box.addEventListener('change', async () => {
          try {
            const info = await API.post('/api/active', { h: node.h, active: box.checked });
            sw.lastChild.textContent = info.active ? '激活' : '未激活';
            SceneTree.markActive(node.h, info.active, !info.activeInHierarchy);
          } catch (e) { box.checked = !box.checked; fail(e); }
        });
        row.append(sw);
      }
      const path = h('div', { class: 'node-path' });
      for (const a of node.ancestors) {
        path.append(h('button', { type: 'button', text: a.v || a.t, onclick: () => Inspector.open(a.h) }), h('span', { text: '/' }));
      }
      path.append(isGo ? h('span', { text: node.name, style: { color: 'var(--fg)', padding: '1px 4px' } })
        : h('button', { type: 'button', text: node.name, onclick: () => Inspector.open(node.h) }));
      row.append(path);
      if (!node.activeInHierarchy && node.active) row.append(h('span', { class: 'pill', text: '父级未激活' }));
      if (node.layer) row.append(h('span', { class: 'pill', title: 'Layer', text: node.layer }));
      if (node.tag && node.tag !== 'Untagged') row.append(h('span', { class: 'pill', title: 'Tag', text: node.tag }));
      if (node.scene) row.append(h('span', { class: 'pill', title: 'Scene', text: node.scene }));
      row.append(iconBtn('copy', '复制层级路径', () => copyText(node.path)));
      head.append(row);
    }
    return head;
  },

  updateHeader() {
    const d = this.data;
    const head = $('.obj-head');
    if (head) head.replaceWith(this.header(d));
    const strip = $('.obj-tabs');
    if (strip) strip.replaceWith(this.tabStrip(d));
  },

  tabStrip(d) {
    const labels = { members: '成员', items: '元素', components: '组件', methods: '方法', preview: '预览', instances: '实例' };
    const counts = { members: d.members.length, items: d.count };
    const strip = h('nav', { class: 'tabs obj-tabs' });
    for (const t of tabsFor(d)) {
      const b = h('button', { class: 'tab' + (t === this.tab ? ' active' : ''), type: 'button' }, labels[t], counts[t] != null ? h('span', { class: 'count', text: String(counts[t]) }) : null);
      b.addEventListener('click', () => {
        this.tab = t;
        if (this.history[this.index]) this.history[this.index].tab = t;
        $$('.obj-tabs .tab').forEach(x => x.classList.toggle('active', x === b));
        this.renderPane();
      });
      strip.append(b);
    }
    return strip;
  },

  async renderPane(refresh = false) {
    const pane = $('#obj-pane');
    if (!pane) return;
    const d = this.data;
    const tab = this.tab;
    const render = {
      members: () => this.membersPane(d, refresh),
      items: () => this.itemsPane(d, refresh),
      components: () => this.componentsPane(d, refresh),
      methods: () => this.methodsPane(d),
      preview: () => this.previewPane(d),
      instances: () => this.instancesPane(d),
    }[tab];
    if (!refresh) pane.replaceChildren(h('div', { class: 'pane-note' }, h('span', { class: 'spin' }), ' 加载中'));
    try {
      const content = await render();
      if (this.data !== d || this.tab !== tab) return;
      if (content) pane.replaceChildren(...[content].flat());
    } catch (e) {
      pane.replaceChildren(h('div', { class: 'pane-note', text: describeError(e) }));
    }
  },

  // ---- members ----
  async membersPane(d, refresh) {
    const f = this.filter;
    const tree = this.tree;
    const ctx = { h: d.h, staticView: d.isStatic };
    const grouped = !d.isStatic || d.bases.length > 1;
    const filter = m => (f.inherited || m.depth === 0) && (f.props || m.kind !== 'prop')
      && (!f.text || m.name.toLowerCase().includes(f.text.toLowerCase()));
    const note = this.membersNote || (this.membersNote = h('div', { class: 'pane-note' }));
    const fill = async () => {
      const frag = await tree.members(this.data.members, 0, ctx, '', { grouped, filter });
      tree.commit();
      tree.el.replaceChildren(frag);
      note.hidden = tree.el.childNodes.length > 0;
      note.textContent = this.data.members.length ? '没有匹配的成员' : '没有公开成员';
    };
    if (refresh && tree.el.isConnected) {
      await fill();
      return null;
    }

    const toolbar = h('div', { class: 'obj-toolbar' });
    const input = h('input', { type: 'search', placeholder: '过滤成员', spellcheck: 'false' });
    input.value = f.text;
    input.addEventListener('input', debounce(() => { f.text = input.value.trim(); fill(); }, 120));
    const check = (label, key, title) => {
      const box = h('input', { type: 'checkbox' });
      box.checked = f[key];
      box.addEventListener('change', () => { f[key] = box.checked; fill(); });
      return h('label', { class: 'check', title }, box, h('span', { text: label }));
    };
    const dot = style => h('span', { style: { display: 'inline-block', width: '6px', height: '6px', borderRadius: '50%', margin: '0 5px 0 10px', ...style } });
    const lazyCount = d.members.filter(m => m.lazy && !m.lazyHint).length;
    const readAll = lazyCount ? h('button', {
      class: 'btn small', type: 'button', title: '静态属性默认不自动读取（单例 getter 可能顺带创建对象）',
      onclick: e => { e.currentTarget.remove(); this.readAll = true; this.refresh(); },
    }, icon('eye'), `读取全部属性（${lazyCount}）`) : null;
    append(toolbar, [h('label', { class: 'search' }, icon('search'), input),
      check('继承成员', 'inherited', '显示基类声明的成员'), check('属性', 'props', '显示属性（字段始终显示）'), readAll,
      h('span', { class: 'spacer' }),
      h('span', { class: 'muted', style: { fontSize: '11.5px' } },
        dot({ background: 'var(--native)' }), 'Il2Cpp 字段', dot({ background: 'var(--fg-3)' }), '托管字段', dot({ border: '1px solid var(--fg-3)' }), '属性')]);
    await fill();
    return [toolbar, h('div', { class: 'obj-pane' }, tree.el, note)];
  },

  // ---- items ----
  async itemsPane(d, refresh) {
    const page = await API.get('/api/items', { h: d.h, offset: 0, limit: 200 });
    const tree = this.tree;
    const frag = await tree.items(page, 0, { h: d.h }, '#items', d.h);
    tree.commit();
    const reuse = refresh && tree.el.isConnected;
    tree.el.replaceChildren(frag);
    if (reuse) return null;
    return h('div', { class: 'obj-pane' }, tree.el);
  },

  // ---- components ----
  openComponents: new Set(['Transform', 'RectTransform']),
  async componentsPane(d, refresh) {
    const comps = await API.get('/api/components', { h: d.node.h });
    const list = h('div', { class: 'comp-list' });
    for (const c of comps) {
      const v = c.value;
      const typeName = v.t || 'Missing';
      const card = h('div', { class: 'comp-card' + (c.missing ? ' missing' : '') });
      const body = h('div', { class: 'comp-body', hidden: true });
      const caret = h('span', { class: 'caret' }, icon('chev'));
      const acts = h('div', { class: 'acts' });
      if (c.enabled != null) {
        const box = h('input', { type: 'checkbox', title: 'enabled' });
        box.checked = c.enabled;
        box.addEventListener('click', e => e.stopPropagation());
        box.addEventListener('change', async () => {
          try { await API.post('/api/enabled', { h: v.h, enabled: box.checked }); } catch (e) { box.checked = !box.checked; fail(e); }
        });
        acts.append(box);
      }
      if (v.h) {
        acts.append(iconBtn('dollar', `在控制台引用 $${v.h}`, () => Console.insert('$' + v.h)));
        acts.append(iconBtn('right', '单独检查', () => Inspector.open(v.h)));
      }
      const head = h('div', { class: 'comp-head' }, caret,
        h('span', { class: 'cname', text: c.missing ? 'Missing Script' : typeName }),
        acts);
      const tree = new VTree();
      const load = async () => {
        body.replaceChildren(h('div', { class: 'loading-row' }, h('span', { class: 'spin' }), ' 加载中'));
        try {
          const data = await API.get('/api/inspect', { h: v.h });
          tree.el.replaceChildren(await tree.members(data.members, 0, { h: v.h }, '', {}));
          tree.commit();
          body.replaceChildren(tree.el);
        } catch (e) {
          body.replaceChildren(msgRow(describeError(e), 0, true));
        }
      };
      const setOpen = async open => {
        card.classList.toggle('open', open);
        body.hidden = !open;
        if (open) { this.openComponents.add(typeName); await load(); } else this.openComponents.delete(typeName);
      };
      if (v.h) head.addEventListener('click', () => setOpen(body.hidden));
      head.addEventListener('contextmenu', e => valueMenu(e, v));
      card.append(head, body);
      list.append(card);
      if (v.h && this.openComponents.has(typeName)) await setOpen(true);
    }
    if (!comps.length) list.append(h('div', { class: 'pane-note', text: '没有组件' }));
    if (refresh) {
      const existing = $('#obj-pane .comp-list');
      if (existing) { existing.replaceWith(list); return null; }
    }
    return list;
  },

  // ---- methods ----
  async methodsPane(d) {
    const methods = this.methodsCache || (this.methodsCache = await API.get('/api/methods', { h: d.h }));
    const toolbar = h('div', { class: 'obj-toolbar' });
    const input = h('input', { type: 'search', placeholder: '过滤方法', spellcheck: 'false' });
    toolbar.append(h('label', { class: 'search' }, icon('search'), input), h('span', { class: 'muted', text: `${methods.length} 个公开方法` }));
    const list = h('div', { class: 'obj-pane' });
    const render = () => {
      const q = input.value.trim().toLowerCase();
      list.replaceChildren();
      let decl = null;
      for (const m of methods) {
        if (q && !m.name.toLowerCase().includes(q)) continue;
        if (m.decl !== decl) {
          decl = m.decl;
          list.append(h('div', { class: 'v-group open', style: { cursor: 'default' } }, h('span', { text: decl }), h('span', { class: 'ln' })));
        }
        list.append(methodRow(d, m));
      }
      if (!list.childNodes.length) list.append(h('div', { class: 'pane-note', text: '没有匹配的方法' }));
    };
    input.addEventListener('input', debounce(render, 100));
    render();
    return [toolbar, list];
  },

  // ---- preview ----
  async previewPane(d) {
    const wrap = h('div', { class: 'preview' });
    const stage = h('div', { class: 'preview-stage' });
    const meta = h('div', { class: 'preview-meta' });
    let max = store.get('previewMax', 1024);
    const sizeSel = h('select', { class: 'select' }, [256, 512, 1024, 2048].map(s => h('option', { value: s, text: `最大 ${s}px` })));
    sizeSel.value = String(max);
    const pixel = h('input', { type: 'checkbox' });
    pixel.checked = store.get('previewPixel', false);
    stage.classList.toggle('pixel', pixel.checked);
    pixel.addEventListener('change', () => { stage.classList.toggle('pixel', pixel.checked); store.set('previewPixel', pixel.checked); });
    let url = null;
    const load = async () => {
      stage.replaceChildren(h('span', { class: 'spin' }));
      try {
        const blob = await API.call('/api/preview', { params: { h: d.h, max }, blob: true });
        if (url) URL.revokeObjectURL(url);
        url = URL.createObjectURL(blob);
        const img = h('img', { src: url, alt: titleOf(d) });
        img.addEventListener('load', () => { dims.textContent = `${img.naturalWidth} × ${img.naturalHeight}`; });
        stage.replaceChildren(img);
        download.href = url;
      } catch (e) {
        stage.replaceChildren(h('span', { class: 'muted', text: describeError(e) }));
      }
    };
    sizeSel.addEventListener('change', () => { max = +sizeSel.value; store.set('previewMax', max); load(); });
    const dims = h('span', { class: 'mono' });
    const download = h('a', { class: 'btn small', download: (titleOf(d) || 'texture') + '.png' }, icon('download'), '下载 PNG');
    meta.append(dims, sizeSel, h('label', { class: 'check' }, pixel, h('span', { text: '像素原样' })), download);
    wrap.append(meta, stage);
    load();
    return wrap;
  },

  // ---- instances ----
  async instancesPane(d) {
    const res = await API.get('/api/find', { h: d.h, limit: 1000 });
    const toolbar = h('div', { class: 'obj-toolbar' });
    const input = h('input', { type: 'search', placeholder: '按名称过滤', spellcheck: 'false' });
    toolbar.append(h('label', { class: 'search' }, icon('search'), input),
      h('span', { class: 'muted', text: res.total > res.items.length ? `共 ${res.total} 个，显示前 ${res.items.length} 个` : `共 ${res.total} 个（含未激活与资源）` }));
    const holder = h('div', { class: 'obj-pane' });
    const tree = new VTree();
    const render = async () => {
      const q = input.value.trim().toLowerCase();
      const items = res.items.map((v, i) => ({ i, value: v })).filter(x => !q || valueText(x.value).toLowerCase().includes(q));
      tree.el.replaceChildren(await tree.items({ items, offset: 0, more: false }, 0, {}, '#inst', d.h));
    };
    input.addEventListener('input', debounce(render, 120));
    await render();
    holder.append(tree.el);
    return [toolbar, holder];
  },
};

function titleOf(d) {
  if (d.text != null) return 'string';
  if (d.isStatic) return d.type.name;
  if (d.node && d.facets.includes('gameobject')) return d.node.name;
  if (d.facets.includes('component')) return `${d.type.name} · ${d.node.name}`;
  const v = d.value;
  if (v.k === 'obj' && v.v) return v.v.length > 60 ? v.v.slice(0, 60) + '…' : v.v;
  if (v.k === 'struct' && v.v) return `${d.type.name} ${v.v}`;
  return d.type.name;
}

function facetKey(d) {
  return (d.isStatic ? 'S:' : 'I:') + [...(d.facets || [])].sort().join(',');
}

function tabsFor(d) {
  if (d.text != null) return ['members'];
  const tabs = [];
  if (d.facets.includes('gameobject')) tabs.push('components');
  if (d.facets.includes('items')) tabs.push('items');
  tabs.push('members', 'methods');
  if (d.facets.includes('preview')) tabs.push('preview');
  if (d.facets.includes('instances')) tabs.push('instances');
  return tabs;
}

function defaultTab(d) {
  if (d.facets?.includes('gameobject')) return 'components';
  if (d.facets?.includes('items') && !d.node) return 'items';
  return 'members';
}

async function openType(full, asm) {
  try {
    const res = await API.get('/api/type', { full, asm });
    await Inspector.open(res.h, { tab: 'members' });
  } catch (e) { fail(e); }
}

function methodRow(d, m) {
  const row = h('div', { class: 'm-row' + (m.ok ? '' : ' disabled') });
  const sig = h('span', { class: 'sig' }, '(',
    m.params.map((p, i) => [i ? ', ' : '', h('span', { class: 'pt', text: p.type }), ' ', h('span', { class: 'pn', text: p.name }), p.optional ? ' = ' + p.default : '']), ')');
  const head = h('div', { class: 'm-head', title: m.ok ? '点击展开调用' : '泛型或 ref/out 参数，请在控制台调用' },
    h('span', { class: 'ret', text: m.ret }),
    h('span', { class: 'mn', text: m.name }), sig,
    m.static ? h('span', { class: 'st', style: { color: 'var(--str)', fontSize: '10px' }, text: 'static' }) : null);
  row.append(head);
  if (!m.ok) return row;

  let form = null;
  head.addEventListener('click', () => {
    if (form) { form.remove(); form = null; return; }
    form = h('div', { class: 'm-form' });
    const inputs = m.params.map(p => {
      let input;
      if (p.edit === 'bool') input = h('select', { class: 'select' }, h('option', { value: 'false', text: 'false' }), h('option', { value: 'true', text: 'true' }));
      else if (p.edit === 'enum') input = h('select', { class: 'select' }, (p.options || []).map(o => h('option', { value: o, text: o })));
      else input = h('input', { class: 'input', type: 'text', spellcheck: 'false', placeholder: p.edit ? (p.edit === 'vec' ? (p.options || []).join(', ') : p.type) : '$句柄 或 null' });
      if (p.optional && p.default != null && input.tagName === 'INPUT') input.value = p.default === 'null' ? '' : p.default;
      if (p.optional && p.default != null && input.tagName === 'SELECT') input.value = p.default.toLowerCase() === 'true' ? 'true' : p.default;
      form.append(h('div', { class: 'm-param' }, h('label', { title: `${p.type} ${p.name}` }, h('span', { class: 'pt', text: p.type }), p.name), input));
      return { p, input };
    });
    const result = h('div', { class: 'm-result', hidden: true });
    const run = h('button', { class: 'btn primary small', type: 'button' }, icon('play'), '调用');
    run.addEventListener('click', async () => {
      const args = inputs.map(({ p, input }) => {
        const text = input.value;
        if (p.edit === 'str') return text;
        if (text.trim() === '' && p.optional) return { default: true };
        if (!p.edit && (text.trim() === '' || text.trim() === 'null')) return null;
        return text;
      });
      run.disabled = true;
      try {
        const v = await API.post('/api/invoke', { h: d.h, id: m.id, args });
        const tree = new VTree({ compact: true });
        tree.el.append(await tree.root(v));
        result.replaceChildren(tree.el);
        result.hidden = false;
      } catch (e) {
        result.replaceChildren(h('div', { class: 'v-msg err', style: { '--depth': 0 }, text: describeError(e) }));
        result.hidden = false;
      } finally {
        run.disabled = false;
      }
    });
    const snippet = h('button', { class: 'btn ghost small', type: 'button', title: '把调用写进控制台' }, icon('terminal'), '写入控制台');
    snippet.addEventListener('click', () => Console.insert(`$${d.h}.${m.name}(${m.params.map(p => p.name).join(', ')})`));
    form.append(h('div', { class: 'm-actions' }, run, snippet), result);
    row.append(form);
    form.querySelector('input,select')?.focus();
  });
  return row;
}

// ============================================================================================
// sidebar: scene tree, types, session
// ============================================================================================

const Sidebar = {
  current: 'scene',
  init() {
    $$('.side-tabs .tab').forEach(b => b.addEventListener('click', () => this.show(b.dataset.side)));
  },
  show(name) {
    this.current = name;
    Mobile.follow('side');
    $$('.side-tabs .tab').forEach(b => b.classList.toggle('active', b.dataset.side === name));
    $$('.side-panel').forEach(p => p.classList.toggle('active', p.dataset.side === name));
    if (name === 'types') Types.ensure();
    if (name === 'session') { Session.refreshVars(); Session.tickWatches(); }
  },
};

/** Keyboard navigation shared by the scene and type trees. */
function treeKeys(container) {
  container.addEventListener('keydown', e => {
    const rows = $$('.t-row', container).filter(r => r.offsetParent !== null && !r.classList.contains('group-head'));
    if (!rows.length) return;
    let i = rows.findIndex(r => r.classList.contains('selected'));
    const select = r => { rows.forEach(x => x.classList.remove('selected')); r.classList.add('selected'); r.scrollIntoView({ block: 'nearest' }); };
    if (e.key === 'ArrowDown') { e.preventDefault(); select(rows[Math.min(rows.length - 1, i + 1)]); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); select(rows[Math.max(0, i - 1)]); }
    else if (e.key === 'ArrowRight' && i >= 0) { e.preventDefault(); const r = rows[i]; if (r._toggle && !r.classList.contains('open')) r._toggle(); else if (rows[i + 1]) select(rows[i + 1]); }
    else if (e.key === 'ArrowLeft' && i >= 0) {
      e.preventDefault();
      const r = rows[i];
      if (r._toggle && r.classList.contains('open')) r._toggle();
      else { const parent = r.parentElement?.parentElement?.previousElementSibling; if (parent?.classList.contains('t-row')) select(parent); }
    } else if (e.key === 'Enter' && i >= 0) { e.preventDefault(); rows[i]._activate?.(); }
  });
}

function nodeIcon(n) {
  const c = n.comps || [];
  if (c.includes('Camera')) return 'eye';
  if (c.includes('SpriteRenderer') || c.includes('Image') || c.includes('RawImage')) return 'image';
  if (c.includes('Canvas')) return 'scene';
  if (c.includes('Light') || c.includes('Light2D')) return 'bolt';
  return 'cube';
}

const SceneTree = {
  el: null,
  expanded: new Set(),
  collapsedScenes: new Set(),
  rows: new Map(),
  selected: null,
  loading: false,
  signature: '',

  init() {
    this.el = $('#scene-tree');
    treeKeys(this.el);
    $('#scene-refresh').addEventListener('click', () => this.load());
    const filter = $('#scene-filter');
    filter.addEventListener('input', debounce(() => this.search(filter.value.trim()), 250));
  },

  async load() {
    if (this.loading) return;
    const q = $('#scene-filter').value.trim();
    if (q) return this.search(q);
    this.loading = true;
    try {
      const scenes = await API.get('/api/scenes');
      const frag = document.createDocumentFragment();
      this.rows.clear();
      for (const s of scenes) frag.append(await this.sceneEl(s));
      const scroll = this.el.scrollTop;
      this.el.replaceChildren(frag);
      this.el.scrollTop = scroll;
    } catch (e) {
      this.el.replaceChildren(h('div', { class: 't-empty', text: describeError(e) }));
    } finally {
      this.loading = false;
    }
  },

  async sceneEl(s) {
    const key = s.name + '#' + s.index;
    const wrap = h('div');
    const open = !this.collapsedScenes.has(key);
    const row = h('div', { class: 't-row scene' + (open ? ' open' : ''), style: { paddingLeft: '6px' } },
      h('span', { class: 'caret' }, icon('chev')), icon(s.special ? 'bookmark' : 'scene', 'i ico'),
      h('span', { class: 'label', text: s.name }),
      h('span', { class: 'meta', text: s.loaded ? String(s.roots.length) : '未加载' }));
    const kids = h('div', { hidden: !open });
    for (const n of s.roots) kids.append(await this.nodeEl(n, 1));
    row._toggle = () => {
      const nowOpen = kids.hidden;
      kids.hidden = !nowOpen;
      row.classList.toggle('open', nowOpen);
      if (nowOpen) this.collapsedScenes.delete(key); else this.collapsedScenes.add(key);
    };
    row._activate = row._toggle;
    row.addEventListener('click', row._toggle);
    wrap.append(row, kids);
    return wrap;
  },

  async nodeEl(n, depth, path = null) {
    const wrap = h('div', { class: 't-node' });
    const caret = h('span', { class: 'caret' + (n.children ? '' : ' empty') }, icon('chev'));
    const row = h('div', {
      class: 't-row' + (!n.active || n.inactive ? ' inactive' : ''),
      style: { paddingLeft: depth * 14 + 4 + 'px' },
      title: (n.comps || []).join(', '),
    }, caret, icon(nodeIcon(n), 'i ico'), h('span', { class: 'label', text: n.name || '(无名)' }),
      path ? h('span', { class: 'path', text: path }) : null,
      n.comps && !path ? h('span', { class: 'meta' }, h('span', { class: 'tag', text: n.comps[0] + (n.comps.length > 1 ? ` +${n.comps.length - 1}` : '') })) : null);
    const kids = h('div');
    row.dataset.h = n.h;
    this.rows.set(n.h, row);
    row._activate = () => { this.select(row); Inspector.open(n.h); };
    row.addEventListener('click', e => {
      if (e.target.closest('.caret') && n.children) return;
      row._activate();
    });
    row.addEventListener('contextmenu', e => {
      e.preventDefault();
      Menu.open(e.clientX, e.clientY, [
        { icon: 'right', label: '检查', run: () => row._activate() },
        { icon: 'dollar', label: `在控制台引用 $${n.h}`, run: () => Console.insert('$' + n.h) },
        { icon: 'pin', label: '收藏', run: () => Session.pin({ k: 'obj', t: 'GameObject', v: n.name, h: n.h }, n.name) },
        '-',
        { icon: 'eye', label: n.active ? '设为未激活' : '设为激活', run: () => this.setActive(n, !n.active, row) },
        { icon: 'copy', label: '复制名称', run: () => copyText(n.name) },
      ]);
    });
    if (n.children) {
      row._toggle = async () => {
        if (row.classList.toggle('open')) {
          this.expanded.add(n.h);
          await this.fillChildren(kids, n, depth);
        } else {
          this.expanded.delete(n.h);
          kids.replaceChildren();
        }
      };
      caret.addEventListener('click', e => { e.stopPropagation(); row._toggle(); });
      row.addEventListener('dblclick', () => row._toggle());
    }
    wrap.append(row, kids);
    if (n.children && this.expanded.has(n.h)) {
      row.classList.add('open');
      await this.fillChildren(kids, n, depth);
    }
    if (this.selected === n.h) row.classList.add('selected');
    return wrap;
  },

  async fillChildren(kids, n, depth) {
    try {
      const children = await API.get('/api/children', { h: n.h });
      const frag = document.createDocumentFragment();
      for (const c of children) frag.append(await this.nodeEl(c, depth + 1));
      kids.replaceChildren(frag);
    } catch (e) {
      kids.replaceChildren(h('div', { class: 't-empty', text: describeError(e) }));
    }
  },

  async setActive(n, active, row) {
    try {
      const info = await API.post('/api/active', { h: n.h, active });
      n.active = info.active;
      row.classList.toggle('inactive', !info.activeInHierarchy);
      if (Inspector.data?.node?.h === n.h) Inspector.refresh();
    } catch (e) { fail(e); }
  },

  markActive(handle, active, inactive) {
    const row = this.rows.get(handle);
    if (row) row.classList.toggle('inactive', !active || inactive);
  },

  select(row) {
    $$('.t-row.selected', this.el).forEach(r => r.classList.remove('selected'));
    row.classList.add('selected');
    this.selected = +row.dataset.h;
  },

  /** Highlights the inspected GameObject if it is visible in the tree. */
  reveal(handle) {
    if (!handle) return;
    this.selected = handle;
    const row = this.rows.get(handle);
    if (row && row.isConnected) {
      $$('.t-row.selected', this.el).forEach(r => r.classList.remove('selected'));
      row.classList.add('selected');
      row.scrollIntoView({ block: 'nearest' });
    }
  },

  async search(q) {
    if (!q) return this.load();
    try {
      const nodes = await API.get('/api/nodes/search', { q, limit: 300 });
      const frag = document.createDocumentFragment();
      frag.append(h('div', { class: 't-row group-head', style: { paddingLeft: '12px' }, text: `${nodes.length >= 300 ? '300+' : nodes.length} 个匹配` }));
      for (const n of nodes) frag.append(await this.nodeEl({ ...n, children: 0 }, 0, n.path));
      if (!nodes.length) frag.append(h('div', { class: 't-empty', text: '没有找到' }));
      this.el.replaceChildren(frag);
    } catch (e) {
      this.el.replaceChildren(h('div', { class: 't-empty', text: describeError(e) }));
    }
  },
};

const Types = {
  el: null,
  loaded: false,
  init() {
    this.el = $('#type-tree');
    treeKeys(this.el);
    const filter = $('#type-filter');
    filter.addEventListener('input', debounce(() => this.search(filter.value.trim()), 160));
    filter.addEventListener('keydown', e => { if (e.key === 'ArrowDown') { e.preventDefault(); this.el.focus(); this.el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown' })); } });
  },

  ensure() { if (!this.loaded) this.loadAssemblies(); },

  async loadAssemblies() {
    this.loaded = true;
    try {
      const asms = await API.get('/api/assemblies');
      const groups = [['game', '游戏'], ['mods', '模组与插件'], ['unity', 'Unity'], ['il2cpp', 'Il2Cpp 系统库'], ['bcl', '.NET']];
      const frag = document.createDocumentFragment();
      for (const [g, label] of groups) {
        const list = asms.filter(a => a.group === g);
        if (!list.length) continue;
        frag.append(h('div', { class: 't-row group-head', style: { paddingLeft: '12px' }, text: label }));
        for (const a of list) frag.append(this.asmEl(a));
      }
      this.el.replaceChildren(frag);
    } catch (e) {
      this.loaded = false;
      this.el.replaceChildren(h('div', { class: 't-empty', text: describeError(e) }));
    }
  },

  asmEl(a) {
    const wrap = h('div');
    const row = h('div', { class: 't-row', style: { paddingLeft: '6px' } },
      h('span', { class: 'caret' }, icon('chev')), icon('package', 'i ico'), h('span', { class: 'label', text: a.name }), h('span', { class: 'meta', text: String(a.types) }));
    const kids = h('div');
    row._toggle = async () => {
      if (!row.classList.toggle('open')) { kids.replaceChildren(); return; }
      kids.replaceChildren(h('div', { class: 't-empty' }, h('span', { class: 'spin' })));
      try {
        const nss = await API.get('/api/types', { asm: a.name });
        const frag = document.createDocumentFragment();
        for (const ns of nss) frag.append(this.nsEl(ns, a.name, nss.length === 1));
        kids.replaceChildren(frag);
      } catch (e) { kids.replaceChildren(h('div', { class: 't-empty', text: describeError(e) })); }
    };
    row._activate = row._toggle;
    row.addEventListener('click', row._toggle);
    wrap.append(row, kids);
    return wrap;
  },

  nsEl(ns, asm, open) {
    const wrap = h('div');
    const row = h('div', { class: 't-row', style: { paddingLeft: '20px' } },
      h('span', { class: 'caret' }, icon('chev')), h('span', { class: 'kind k-ns', text: 'N' }),
      h('span', { class: 'label', text: ns.ns || '(全局命名空间)' }), h('span', { class: 'meta', text: String(ns.types.length) }));
    const kids = h('div');
    row._toggle = () => {
      if (!row.classList.toggle('open')) { kids.replaceChildren(); return; }
      kids.replaceChildren(...ns.types.map(t => this.typeEl(t, 34)));
    };
    row._activate = row._toggle;
    row.addEventListener('click', row._toggle);
    wrap.append(row, kids);
    if (open) row._toggle();
    return wrap;
  },

  typeEl(t, indent, showNs = false) {
    const kindLetter = { class: 'C', struct: 'S', enum: 'E', interface: 'I', static: 'St', delegate: 'D' }[t.kind] || 'C';
    const row = h('div', { class: 't-row', style: { paddingLeft: indent + 'px' }, title: `${t.full}\n${t.asm}` },
      h('span', { class: 'kind k-' + t.kind, text: kindLetter }),
      h('span', { class: 'label', text: t.name }),
      showNs ? h('span', { class: 'path', text: t.ns }) : null,
      t.unity ? h('span', { class: 'meta', text: 'Unity' }) : null);
    row._activate = () => { $$('.t-row.selected', this.el).forEach(r => r.classList.remove('selected')); row.classList.add('selected'); openType(t.full, t.asm); };
    row.addEventListener('click', row._activate);
    row.addEventListener('contextmenu', e => {
      e.preventDefault();
      Menu.open(e.clientX, e.clientY, [
        { icon: 'braces', label: '查看静态成员', run: row._activate },
        { icon: 'terminal', label: '写入控制台', run: () => Console.insert(t.full.replace(/\+/g, '.')) },
        { icon: 'copy', label: '复制完整类型名', run: () => copyText(t.full) },
      ]);
    });
    return row;
  },

  async search(q) {
    if (!q) { this.loaded = false; return this.loadAssemblies(); }
    try {
      const types = await API.get('/api/types/search', { q, limit: 120 });
      const frag = document.createDocumentFragment();
      for (const t of types) frag.append(this.typeEl(t, 10, true));
      if (!types.length) frag.append(h('div', { class: 't-empty', text: '没有匹配的类型' }));
      this.el.replaceChildren(frag);
    } catch (e) {
      this.el.replaceChildren(h('div', { class: 't-empty', text: describeError(e) }));
    }
  },
};

const Session = {
  pins: [],
  watches: [],
  watchTrees: new Map(),
  watchTimer: 0,
  boot: null,

  init() {
    this.watches = store.get('watches', []);
    $('#watch-form').addEventListener('submit', e => {
      e.preventDefault();
      const input = $('#watch-input');
      if (input.value.trim()) this.addWatch(input.value.trim());
      input.value = '';
    });
    $('#vars-refresh').addEventListener('click', () => this.refreshVars());
    this.watchTimer = setInterval(() => {
      if (!document.hidden && Sidebar.current === 'session' && $('#watch-live').checked) this.tickWatches();
    }, 1000);
    this.renderPins();
    this.renderWatches();
  },

  /** Handles only mean something for one game process; drop pins from a previous boot. */
  setBoot(boot) {
    const saved = store.get('pins', null, 'session');
    if (this.boot === null) {
      this.boot = boot;
      if (saved && saved.boot === boot) this.pins = saved.pins;
      this.renderPins();
      return false;
    }
    if (this.boot !== boot) {
      this.boot = boot;
      this.pins = [];
      this.savePins();
      this.renderPins();
      return true;
    }
    return false;
  },

  savePins() { store.set('pins', { boot: this.boot, pins: this.pins }, 'session'); },

  pin(v, label) {
    if (!v?.h) return;
    if (!this.pins.some(p => p.v.h === v.h)) this.pins.unshift({ v, label: label || valueText(v) });
    this.savePins();
    this.renderPins();
    toast('已收藏', 'ok');
  },

  renderPins() {
    const el = $('#pins');
    $('#pin-count').textContent = this.pins.length ? String(this.pins.length) : '';
    el.replaceChildren();
    if (!this.pins.length) { el.append(h('div', { class: 'empty', text: '右键对象选择“收藏”，便于来回切换' })); return; }
    for (const p of this.pins) {
      el.append(h('div', { class: 'l-item' },
        h('div', { class: 'l-head', style: { cursor: 'pointer' }, onclick: () => Inspector.open(p.v.h) },
          h('span', { class: 'name', text: p.label }),
          h('span', { class: 'val' }, renderValue({ ...p.v, v: null }, { link: false })),
          h('span', { class: 'acts' },
            iconBtn('dollar', '在控制台引用', () => Console.insert('$' + p.v.h)),
            iconBtn('x', '移除', () => { this.pins = this.pins.filter(x => x !== p); this.savePins(); this.renderPins(); })))));
    }
  },

  addWatch(expr) {
    if (!this.watches.includes(expr)) this.watches.push(expr);
    store.set('watches', this.watches);
    this.renderWatches();
    Sidebar.show('session');
  },

  renderWatches() {
    const el = $('#watches');
    el.replaceChildren();
    if (!this.watches.length) { el.append(h('div', { class: 'empty', text: '表达式每秒求值一次，可使用静态成员与 $句柄' })); return; }
    for (const expr of this.watches) {
      const tree = this.watchTrees.get(expr) || new VTree({ compact: true });
      this.watchTrees.set(expr, tree);
      const item = h('div', { class: 'l-item', dataset: { expr } },
        h('div', { class: 'l-head' },
          h('span', { class: 'name', text: expr, title: expr }),
          h('span', { class: 'val' }),
          h('span', { class: 'acts' },
            iconBtn('terminal', '写入控制台', () => Console.insert(expr)),
            iconBtn('x', '移除', () => { this.watches = this.watches.filter(x => x !== expr); store.set('watches', this.watches); this.watchTrees.delete(expr); this.renderWatches(); }))),
        tree.el);
      el.append(item);
    }
    this.tickWatches();
  },

  async tickWatches() {
    if (!this.watches.length || this.watchBusy || !Auth.ready) return;
    this.watchBusy = true;
    try {
      const results = await API.post('/api/watch', { exprs: this.watches });
      results.forEach(async (r, i) => {
        const expr = this.watches[i];
        const item = $(`#watches .l-item[data-expr="${CSS.escape(expr)}"]`);
        if (!item) return;
        const val = item.querySelector('.val');
        const tree = this.watchTrees.get(expr);
        if (r.error) {
          val.replaceChildren(h('span', { class: 'val k-err', text: '⚠', title: r.error }));
          tree.el.replaceChildren(h('div', { class: 'v-msg err', style: { '--depth': 0 }, text: r.error }));
          return;
        }
        val.replaceChildren();
        const node = await tree.root(r.value, expr);
        tree.commit();
        tree.el.replaceChildren(node);
      });
    } catch (e) {
      /* status indicator already reports connectivity */
    } finally {
      this.watchBusy = false;
    }
  },

  async refreshVars() {
    const el = $('#vars');
    try {
      const vars = await API.get('/api/vars');
      el.replaceChildren();
      if (!vars.length) { el.append(h('div', { class: 'empty', text: '在控制台用 var x = … 声明的变量会出现在这里' })); return; }
      for (const v of vars) {
        const tree = new VTree({ compact: true });
        tree.el.append(await tree.root(v.value, 'var:' + v.name));
        el.append(h('div', { class: 'l-item' },
          h('div', { class: 'l-head' }, h('span', { class: 'name', text: v.name }), h('span', { class: 'muted', style: { fontSize: '11px' }, text: v.type }),
            h('span', { class: 'acts', style: { marginLeft: 'auto' } }, iconBtn('terminal', '写入控制台', () => Console.insert(v.name)))),
          tree.el));
      }
    } catch (e) {
      el.replaceChildren(h('div', { class: 'empty', text: describeError(e) }));
    }
  },
};

// ============================================================================================
// console
// ============================================================================================

const CS_KEYWORDS = new Set(('abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while async await nameof when where yield get set value dynamic record init').split(' '));

const TOKEN_RE = /(\/\/[^\n]*|\/\*[\s\S]*?(?:\*\/|$))|(\$?@"(?:[^"]|"")*"?|@\$"(?:[^"]|"")*"?|\$?"(?:[^"\\\n]|\\.)*"?|'(?:[^'\\\n]|\\.)*'?)|(\$\d+\b|\$_(?![A-Za-z0-9_]))|(\b0x[0-9a-fA-F]+\b|\b\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMlLuU]*\b)|([A-Za-z_][A-Za-z0-9_]*)/g;

function escapeHtml(s) {
  return s.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

function highlight(code) {
  let out = '';
  let last = 0;
  TOKEN_RE.lastIndex = 0;
  let m;
  while ((m = TOKEN_RE.exec(code))) {
    out += escapeHtml(code.slice(last, m.index));
    const text = escapeHtml(m[0]);
    if (m[1]) out += `<span class="tk-com">${text}</span>`;
    else if (m[2]) out += `<span class="tk-str">${text}</span>`;
    else if (m[3]) out += `<span class="tk-h">${text}</span>`;
    else if (m[4]) out += `<span class="tk-num">${text}</span>`;
    else if (CS_KEYWORDS.has(m[0])) out += `<span class="tk-kw">${text}</span>`;
    else if (/^[A-Z]/.test(m[0])) out += code[TOKEN_RE.lastIndex] === '(' ? `<span class="tk-fn">${text}</span>` : `<span class="tk-type">${text}</span>`;
    else out += text;
    last = TOKEN_RE.lastIndex;
  }
  return out + escapeHtml(code.slice(last));
}

/** Same heuristic as scripts/console.py: unbalanced brackets, open strings or a trailing operator mean "keep typing". */
function isComplete(code) {
  let depth = 0;
  let str = null;
  let esc = false;
  let line = false;
  let block = false;
  let last = '';
  for (let i = 0; i < code.length; i++) {
    const c = code[i];
    const n = code[i + 1];
    if (line) { if (c === '\n') line = false; continue; }
    if (block) { if (c === '*' && n === '/') { block = false; i++; } continue; }
    if (str) {
      if (esc) esc = false;
      else if (c === '\\' && str !== '@') esc = true;
      else if (str === '@' && c === '"') { if (n === '"') i++; else str = null; }
      else if (c === str) str = null;
      continue;
    }
    if (c === '/' && n === '/') { line = true; i++; continue; }
    if (c === '/' && n === '*') { block = true; i++; continue; }
    if (c === '@' && n === '"') { str = '@'; i++; last = '"'; continue; }
    if (c === '"' || c === "'") { str = c; last = c; continue; }
    if ('([{'.includes(c)) depth++;
    else if (')]}'.includes(c)) depth--;
    if (!/\s/.test(c)) last = c;
  }
  if (str || block || depth > 0) return false;
  const tail = code.trimEnd();
  if (tail.endsWith('++') || tail.endsWith('--')) return true;
  return !(last && ',.+-*/%=&|^!?<>(:'.includes(last));
}

const PROGRAM_TEMPLATE = `using UnityEngine;

public static class Payload
{
    public static object Execute()
    {
        return null;
    }
}`;

const Console = {
  input: null,
  hl: null,
  out: null,
  mode: 'script',
  history: [],
  hIndex: -1,
  draft: '',
  running: false,
  cmp: null,
  cmpSeq: 0,
  charW: 7.5,

  init() {
    this.input = $('#editor-input');
    this.hl = $('#editor-hl');
    this.out = $('#console-out');
    this.history = store.get('history', []);
    this.mode = store.get('consoleMode', 'script');
    this.input.value = store.get('draft', '');
    this.measure();
    this.sync();
    this.setMode(this.mode, false);

    this.input.addEventListener('input', () => { this.sync(); store.set('draft', this.input.value); this.onInput(); });
    this.input.addEventListener('scroll', () => { this.hl.parentElement.scrollTop = this.input.scrollTop; this.hl.parentElement.scrollLeft = this.input.scrollLeft; });
    this.input.addEventListener('keydown', e => this.onKey(e));
    this.input.addEventListener('blur', () => setTimeout(() => this.closeCompletion(), 150));
    $('#console-run').addEventListener('click', () => this.run());
    $('#console-clear').addEventListener('click', () => this.clear());
    $('#console-reset').addEventListener('click', () => this.reset());
    $('#snippets-open').addEventListener('click', e => this.snippetsMenu(e));
    $$('#console-mode button').forEach(b => b.addEventListener('click', () => this.setMode(b.dataset.mode)));
    this.system('会话式 C#：裸语句即可执行，变量在提交间保留；以不带分号的表达式结尾会返回其值。');
  },

  measure() {
    const probe = h('span', { class: 'mono', style: { font: getComputedStyle(this.input).font, position: 'absolute', visibility: 'hidden', whiteSpace: 'pre' }, text: 'x'.repeat(80) });
    document.body.append(probe);
    this.charW = probe.getBoundingClientRect().width / 80 || 7.5;
    probe.remove();
  },

  setMode(mode, insertTemplate = true) {
    this.mode = mode;
    store.set('consoleMode', mode);
    $$('#console-mode button').forEach(b => b.classList.toggle('active', b.dataset.mode === mode));
    $('#console-hint').textContent = mode === 'program'
      ? 'Ctrl+Enter 执行 · 完整程序：public static class Payload { public static object Execute() }，每次独立编译'
      : 'Enter 执行 · Shift+Enter 换行 · Ctrl+Space 补全 · ↑↓ 历史 · $12 引用对象 · $_ 上次结果';
    if (mode === 'program' && insertTemplate && !this.input.value.trim()) { this.input.value = PROGRAM_TEMPLATE; this.sync(); }
  },

  sync() {
    this.hl.innerHTML = highlight(this.input.value) + '\n';
    this.input.style.height = 'auto';
    this.input.style.height = Math.min(260, Math.max(37, this.input.scrollHeight + 2)) + 'px';
  },

  focus() {
    Drawer.show('console');
    this.input.focus();
  },

  insert(text) {
    Drawer.show('console');
    const el = this.input;
    const start = el.selectionStart ?? el.value.length;
    const end = el.selectionEnd ?? el.value.length;
    el.setRangeText(text, start, end, 'end');
    this.sync();
    store.set('draft', el.value);
    el.focus();
  },

  set(text) {
    this.input.value = text;
    this.sync();
    this.input.setSelectionRange(text.length, text.length);
    store.set('draft', text);
  },

  onKey(e) {
    const el = this.input;
    if (this.cmp && !this.cmp.hidden) {
      if (e.key === 'ArrowDown') { e.preventDefault(); this.moveCompletion(1); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); this.moveCompletion(-1); return; }
      if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); this.acceptCompletion(); return; }
      if (e.key === 'Escape') { e.preventDefault(); this.closeCompletion(); return; }
    }
    if (e.key === ' ' && e.ctrlKey) { e.preventDefault(); this.complete(true); return; }
    if (e.key === 'Enter') {
      const runNow = e.ctrlKey || e.metaKey || (!e.shiftKey && this.mode === 'script' && isComplete(el.value) && el.value.trim());
      if (runNow) { e.preventDefault(); this.run(); return; }
      e.preventDefault();
      const before = el.value.slice(0, el.selectionStart);
      const indent = (before.split('\n').pop().match(/^\s*/) || [''])[0] + (/[{(\[]\s*$/.test(before) ? '    ' : '');
      el.setRangeText('\n' + indent, el.selectionStart, el.selectionEnd, 'end');
      this.sync();
      return;
    }
    if (e.key === 'Tab') {
      e.preventDefault();
      el.setRangeText('    ', el.selectionStart, el.selectionEnd, 'end');
      this.sync();
      return;
    }
    if (e.key === 'ArrowUp' && !el.value.slice(0, el.selectionStart).includes('\n') && this.history.length) {
      e.preventDefault();
      if (this.hIndex === -1) { this.draft = el.value; this.hIndex = this.history.length; }
      this.hIndex = Math.max(0, this.hIndex - 1);
      this.set(this.history[this.hIndex]);
      return;
    }
    if (e.key === 'ArrowDown' && !el.value.slice(el.selectionEnd).includes('\n') && this.hIndex !== -1) {
      e.preventDefault();
      this.hIndex++;
      if (this.hIndex >= this.history.length) { this.hIndex = -1; this.set(this.draft); } else this.set(this.history[this.hIndex]);
      return;
    }
    if (e.key === 'l' && e.ctrlKey) { e.preventDefault(); this.clear(); }
  },

  onInput() {
    const el = this.input;
    const caret = el.selectionStart;
    const prev = el.value[caret - 1];
    if (prev === '.' && this.mode === 'script') { this.complete(false); return; }
    if (this.cmp && !this.cmp.hidden) this.filterCompletion();
  },

  // ---- completion ----
  async complete(explicit) {
    const code = this.input.value;
    const pos = this.input.selectionStart;
    const seq = ++this.cmpSeq;
    try {
      const res = await API.post('/api/complete', { code, pos });
      if (seq !== this.cmpSeq) return;
      this.cmp = { from: res.from, all: res.items, items: [], sel: 0, hidden: false };
      this.filterCompletion();
      if (!this.cmp.items.length && explicit) toast('没有可用的补全');
    } catch (e) {
      if (explicit) fail(e);
    }
  },

  filterCompletion() {
    const c = this.cmp;
    if (!c) return;
    const caret = this.input.selectionStart;
    const prefix = this.input.value.slice(c.from, caret);
    if (caret < c.from || /[^A-Za-z0-9_]/.test(prefix)) { this.closeCompletion(); return; }
    const p = prefix.toLowerCase();
    c.items = c.all.filter(it => !p || it.label.toLowerCase().startsWith(p) || humps(it.label, p)).slice(0, 200);
    c.prefix = prefix;
    c.sel = 0;
    if (!c.items.length) { this.closeCompletion(); return; }
    this.renderCompletion();
  },

  renderCompletion() {
    const c = this.cmp;
    const box = $('#completion');
    const kindLetter = { method: 'M', prop: 'P', field: 'F', local: 'V', const: 'C', enum: 'E', class: 'T', struct: 'S', interface: 'I', delegate: 'D', ns: 'N', keyword: 'K', event: 'Ev' };
    box.replaceChildren(...c.items.map((it, i) => {
      const n = c.prefix.length;
      const label = it.label.toLowerCase().startsWith(c.prefix.toLowerCase()) && n
        ? [h('b', { text: it.label.slice(0, n) }), it.label.slice(n)] : [it.label];
      const row = h('div', { class: 'cmp-item' + (i === c.sel ? ' active' : '') },
        h('span', { class: 'ck ' + it.kind, text: kindLetter[it.kind] || '?' }),
        h('span', { class: 'cl' }, label),
        it.detail ? h('span', { class: 'cd', text: it.detail, title: it.detail }) : null);
      row.addEventListener('mousedown', e => { e.preventDefault(); c.sel = i; this.acceptCompletion(); });
      return row;
    }));
    box.hidden = false;
    c.hidden = false;
    // Position at the caret, opening upwards (the editor sits at the bottom of the drawer).
    const el = this.input;
    const before = el.value.slice(0, c.from);
    const lineIdx = before.split('\n').length - 1;
    const col = before.length - before.lastIndexOf('\n') - 1;
    const style = getComputedStyle(el);
    const lineH = parseFloat(style.lineHeight) || 19;
    const x = parseFloat(style.paddingLeft) + col * this.charW - el.scrollLeft;
    const y = parseFloat(style.paddingTop) + lineIdx * lineH - el.scrollTop;
    box.style.left = Math.max(4, Math.min(x - 30, el.clientWidth - 320)) + 'px';
    box.style.bottom = (el.offsetHeight - y + 2) + 'px';
    box.style.top = 'auto';
    box.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
  },

  moveCompletion(delta) {
    const c = this.cmp;
    c.sel = (c.sel + delta + c.items.length) % c.items.length;
    $$('#completion .cmp-item').forEach((r, i) => r.classList.toggle('active', i === c.sel));
    $('#completion .active')?.scrollIntoView({ block: 'nearest' });
  },

  acceptCompletion() {
    const c = this.cmp;
    const it = c?.items[c.sel];
    if (!it) return this.closeCompletion();
    const el = this.input;
    el.setRangeText(it.label, c.from, el.selectionStart, 'end');
    this.closeCompletion();
    this.sync();
    store.set('draft', el.value);
  },

  closeCompletion() {
    if (this.cmp) this.cmp.hidden = true;
    $('#completion').hidden = true;
  },

  // ---- running ----
  async run() {
    const code = this.input.value;
    if (!code.trim() || this.running) return;
    this.closeCompletion();
    this.running = true;
    const btn = $('#console-run');
    btn.classList.add('busy');
    if (this.history[this.history.length - 1] !== code) {
      this.history.push(code);
      if (this.history.length > 200) this.history.shift();
      store.set('history', this.history);
    }
    this.hIndex = -1;
    this.set('');
    const entry = this.entry('in', '›', h('div', { class: 'code', html: highlight(code), title: '点击放回编辑器', onclick: () => { this.set(code); this.input.focus(); } }));
    const pending = this.entry('sys', '', h('span', {}, h('span', { class: 'spin' }), ' 执行中'));
    try {
      const res = await API.post('/api/eval', { code, mode: this.mode });
      pending.remove();
      if (res.output) this.entry('out', '', h('pre', { text: res.output.replace(/\n$/, '') }));
      if (!res.success) this.error(res.error || '执行失败');
      else if (res.value) {
        const tree = new VTree({ compact: true });
        tree.el.append(await tree.root(res.value));
        this.entry('res', '‹', tree.el, h('span', { class: 'meta', text: `${res.ms} ms` }));
      } else {
        this.entry('res', '‹', h('span', { class: 'val k-void', text: '完成' }), h('span', { class: 'meta', text: `${res.ms} ms` }));
      }
      if (this.mode === 'script' && Sidebar.current === 'session') Session.refreshVars();
    } catch (e) {
      pending.remove();
      this.error(describeError(e));
    } finally {
      this.running = false;
      btn.classList.remove('busy');
      entry.scrollIntoView({ block: 'nearest' });
      this.out.scrollTop = this.out.scrollHeight;
    }
  },

  entry(kind, gutter, ...content) {
    const el = h('div', { class: 'c-entry ' + kind }, h('span', { class: 'gut', text: gutter }), h('div', {}, ...content));
    this.out.append(el);
    while (this.out.childElementCount > 400) this.out.firstElementChild.remove();
    this.out.scrollTop = this.out.scrollHeight;
    return el;
  },

  system(text) { return this.entry('sys', '', h('span', { text })); },

  /** Compiler errors become a position / id / message grid; runtime exceptions show the first line with the stack folded. */
  error(text) {
    const diags = [];
    for (const line of text.split('\n')) {
      const m = line.match(/^\((\d+),(\d+)\): (error|warning) (CS\d+): (.*)$/);
      if (m) diags.push(m);
    }
    if (diags.length) {
      this.entry('err', '✕', h('div', { class: 'diag' }, diags.map(m => [
        h('span', { class: 'pos', text: `${m[1]}:${m[2]}` }), h('span', { class: 'id', text: m[4] }), h('span', { class: 'msg', text: m[5] })])));
      return;
    }
    const lines = text.split('\n');
    const first = lines[0].replace(/^System\.Reflection\.TargetInvocationException: [^\n]*?---> /, '');
    if (lines.length > 1) this.entry('err', '✕', h('details', {}, h('summary', { text: first }), h('pre', { text: lines.slice(1).join('\n') })));
    else this.entry('err', '✕', h('pre', { text: first }));
  },

  clear() { this.out.replaceChildren(); },

  async reset() {
    if (!confirm('清空控制台会话中声明的所有变量和类型？')) return;
    try {
      await API.post('/api/script/reset');
      this.system('会话已重置');
      Session.refreshVars();
    } catch (e) { fail(e); }
  },

  snippetsMenu(e) {
    const saved = store.get('snippets', []);
    const items = saved.map(s => ({ icon: 'bookmark', label: s.name, run: () => { this.set(s.code); this.focus(); } }));
    if (items.length) items.push('-');
    items.push({ icon: 'plus', label: '把当前代码存为片段', run: () => {
      const code = this.input.value.trim();
      if (!code) { toast('编辑器是空的'); return; }
      const name = prompt('片段名称', code.split('\n')[0].slice(0, 40));
      if (!name) return;
      store.set('snippets', [...saved.filter(s => s.name !== name), { name, code }]);
      toast('已保存片段', 'ok');
    } });
    if (saved.length) items.push({ icon: 'trash', label: '删除片段…', run: () => {
      const name = prompt('要删除的片段名称：\n' + saved.map(s => s.name).join('\n'));
      if (name) store.set('snippets', saved.filter(s => s.name !== name));
    } });
    const r = e.currentTarget.getBoundingClientRect();
    Menu.open(r.left, r.top - 8 - (items.length * 30), items);
  },
};

function humps(label, p) {
  let j = 0;
  for (let i = 0; i < label.length && j < p.length; i++) {
    if ((i === 0 || (label[i] >= 'A' && label[i] <= 'Z')) && label[i].toLowerCase() === p[j]) j++;
  }
  return j === p.length;
}

// ============================================================================================
// logs
// ============================================================================================

const Logs = {
  entries: [],
  next: -1,
  levels: new Set(['error', 'warn', 'info']),
  sources: new Set(),
  unread: 0,
  timer: 0,

  init() {
    $$('#log-levels button').forEach(b => {
      b.classList.toggle('active', this.levels.has(b.dataset.level));
      b.addEventListener('click', () => {
        if (this.levels.has(b.dataset.level)) this.levels.delete(b.dataset.level); else this.levels.add(b.dataset.level);
        b.classList.toggle('active');
        this.renderAll();
      });
    });
    $('#log-source').addEventListener('change', () => this.renderAll());
    $('#log-filter').addEventListener('input', debounce(() => this.renderAll(), 150));
    $('#log-clear').addEventListener('click', () => { this.entries = []; this.renderAll(); });
    $('#log-full').addEventListener('click', () => this.full());
  },

  start() {
    clearInterval(this.timer);
    this.poll();
    this.timer = setInterval(() => { if (!document.hidden) this.poll(); }, 1000);
  },

  async poll() {
    if (this.polling) return;
    this.polling = true;
    try {
      const res = await API.get('/api/logs', { after: this.next, limit: 2000 });
      if (res.next - 1 < this.next) {
        // The game restarted and its log sequence began again.
        this.next = -1;
        this.entries.push({ sep: true, text: '— 游戏已重启 —' });
        return;
      }
      if (res.dropped) this.entries.push({ sep: true, text: '…部分日志未取到（缓冲区已轮转）' });
      const list = $('#log-list');
      const stick = list.scrollHeight - list.scrollTop - list.clientHeight < 40;
      const frag = document.createDocumentFragment();
      for (const e of res.entries) {
        this.entries.push(e);
        if (!this.sources.has(e.src)) {
          this.sources.add(e.src);
          const sel = $('#log-source');
          const opts = Array.from(this.sources).sort((a, b) => a.localeCompare(b));
          const value = sel.value;
          sel.replaceChildren(h('option', { value: '', text: '全部来源' }), ...opts.map(s => h('option', { value: s, text: s })));
          sel.value = value;
        }
        if ((e.lv === 'error' || e.lv === 'fatal') && Drawer.current !== 'logs') this.unread++;
        if (this.passes(e)) frag.append(this.row(e));
      }
      this.next = res.next - 1;
      if (this.entries.length > 5000) { this.entries.splice(0, this.entries.length - 5000); }
      list.append(frag);
      while (list.childElementCount > 5000) list.firstElementChild.remove();
      if (stick && $('#log-follow').checked) list.scrollTop = list.scrollHeight;
      this.badge();
    } catch (e) {
      /* connectivity shown in the status bar */
    } finally {
      this.polling = false;
    }
  },

  passes(e) {
    if (e.sep) return true;
    const lv = e.lv === 'fatal' ? 'error' : e.lv === 'message' ? 'info' : e.lv;
    if (!this.levels.has(lv)) return false;
    const src = $('#log-source').value;
    if (src && e.src !== src) return false;
    const q = $('#log-filter').value.trim().toLowerCase();
    return !q || e.text.toLowerCase().includes(q) || e.src.toLowerCase().includes(q);
  },

  row(e) {
    if (e.sep) return h('div', { class: 'log-sep', text: e.text });
    const d = new Date(e.t);
    const pad = (n, w = 2) => String(n).padStart(w, '0');
    const time = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
    const msg = h('div', { class: 'msg' + (e.text.split('\n').length > 4 || e.text.length > 600 ? ' clamp' : ''), text: e.text });
    msg.addEventListener('click', () => msg.classList.remove('clamp'));
    const labels = { fatal: 'FATAL', error: 'ERR', warn: 'WARN', message: 'MSG', info: 'INFO', debug: 'DBG' };
    return h('div', { class: 'lg l-' + e.lv },
      h('span', { class: 'tm', text: time }), h('span', { class: 'lv', text: labels[e.lv] || e.lv }), h('span', { class: 'src', text: e.src, title: e.src }), msg);
  },

  renderAll() {
    const list = $('#log-list');
    const frag = document.createDocumentFragment();
    for (const e of this.entries) if (this.passes(e)) frag.append(this.row(e));
    list.replaceChildren(frag);
    list.scrollTop = list.scrollHeight;
  },

  badge() {
    const b = $('#log-badge');
    b.hidden = this.unread === 0;
    b.textContent = this.unread > 99 ? '99+' : String(this.unread);
  },

  async full() {
    try {
      const text = await API.call('/logcat', { text: true });
      const tail = text.length > 400000 ? '…（仅显示最后 400 KB）\n' + text.slice(-400000) : text;
      Modal.open('LogOutput.log', h('pre', { text: tail }), [h('button', { class: 'btn small', type: 'button', onclick: () => copyText(text) }, icon('copy'), '复制全部')]);
      const body = $('#modal-body');
      body.scrollTop = body.scrollHeight;
    } catch (e) { fail(e); }
  },
};

// ============================================================================================
// drawer, layout, palette, status, auth
// ============================================================================================

const Drawer = {
  current: 'console',
  init() {
    $$('.drawer-tabs .tab').forEach(b => b.addEventListener('click', () => this.show(b.dataset.drawer)));
    $('#drawer-max').addEventListener('click', () => this.toggleMax());
  },
  show(name) {
    this.current = name;
    Mobile.follow('drawer');
    $$('.drawer-tabs .tab').forEach(b => b.classList.toggle('active', b.dataset.drawer === name));
    $$('.drawer-panel').forEach(p => p.classList.toggle('active', p.dataset.drawer === name));
    if (name === 'logs') {
      Logs.unread = 0;
      Logs.badge();
      const list = $('#log-list');
      list.scrollTop = list.scrollHeight;
    }
  },
  toggleMax() { $('#drawer').classList.toggle('max'); },
};

/** Phones show one pane at a time; jump to the pane that just got new content. */
const Mobile = {
  narrow: matchMedia('(max-width: 720px)'),
  init() {
    $$('#mobile-nav button').forEach(b => b.addEventListener('click', () => this.show(b.dataset.pane)));
  },
  show(pane) {
    $('#app').dataset.pane = pane;
    $$('#mobile-nav button').forEach(b => b.classList.toggle('active', b.dataset.pane === pane));
  },
  follow(pane) {
    if (this.narrow.matches) this.show(pane);
  },
};

const Layout = {
  init() {
    const side = $('#sidebar');
    const drawer = $('#drawer');
    const sw = store.get('sideW');
    const dh = store.get('drawerH');
    if (sw) side.style.setProperty('--side-w', sw + 'px');
    if (dh) drawer.style.setProperty('--drawer-h', dh + 'px');
    this.drag($('#side-resizer'), 'col', e => {
      const w = Math.max(200, Math.min(innerWidth * 0.6, e.clientX));
      side.style.setProperty('--side-w', w + 'px');
      store.set('sideW', w);
    });
    this.drag($('#drawer-resizer'), 'row', e => {
      const rect = $('#main').getBoundingClientRect();
      const hgt = Math.max(120, Math.min(rect.height - 120, rect.bottom - e.clientY));
      drawer.classList.remove('max');
      drawer.style.setProperty('--drawer-h', hgt + 'px');
      store.set('drawerH', hgt);
    });
  },
  drag(handle, dir, move) {
    handle.addEventListener('mousedown', e => {
      e.preventDefault();
      handle.classList.add('dragging');
      document.body.classList.add('dragging-' + dir);
      const up = () => {
        handle.classList.remove('dragging');
        document.body.classList.remove('dragging-' + dir);
        removeEventListener('mousemove', move);
        removeEventListener('mouseup', up);
      };
      addEventListener('mousemove', move);
      addEventListener('mouseup', up);
    });
  },
};

const Theme = {
  init() {
    const saved = store.get('theme');
    const prefersLight = matchMedia('(prefers-color-scheme: light)').matches;
    this.set(saved || (prefersLight ? 'light' : 'dark'), false);
    $('#theme-toggle').addEventListener('click', () => this.toggle());
  },
  set(theme, save = true) {
    document.documentElement.dataset.theme = theme;
    if (save) store.set('theme', theme);
  },
  toggle() { this.set(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light'); },
};

const COMMANDS = [
  { label: '刷新场景树', icon: 'refresh', run: () => { Sidebar.show('scene'); SceneTree.load(); } },
  { label: '聚焦控制台', icon: 'terminal', sub: 'Ctrl+`', run: () => Console.focus() },
  { label: '打开日志', icon: 'file', run: () => Drawer.show('logs') },
  { label: '重置控制台会话', icon: 'rotate', run: () => Console.reset() },
  { label: '清空对象句柄', icon: 'trash', run: async () => { try { await API.post('/api/handles/clear'); Inspector.history = []; Inspector.index = -1; Inspector.welcome(); SceneTree.load(); toast('句柄已清空', 'ok'); } catch (e) { fail(e); } } },
  { label: '切换明暗主题', icon: 'contrast', run: () => Theme.toggle() },
  { label: '最大化 / 还原底部面板', icon: 'maximize', run: () => Drawer.toggleMax() },
  { label: '使用说明', icon: 'help', run: () => showAbout() },
  { label: '退出登录', icon: 'x', run: () => Auth.logout() },
];

const Palette = {
  items: [],
  sel: 0,
  seq: 0,
  init() {
    $('#palette-open').addEventListener('click', () => this.open());
    $('#palette').addEventListener('mousedown', e => { if (e.target.id === 'palette') this.close(); });
    const input = $('#palette-input');
    input.addEventListener('input', debounce(() => this.query(input.value), 120));
    input.addEventListener('keydown', e => {
      if (e.key === 'ArrowDown') { e.preventDefault(); this.move(1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); this.move(-1); }
      else if (e.key === 'Enter') { e.preventDefault(); this.accept(); }
      else if (e.key === 'Escape') { e.preventDefault(); this.close(); }
    });
  },
  open() {
    $('#palette').hidden = false;
    const input = $('#palette-input');
    input.value = '';
    input.focus();
    this.query('');
  },
  close() { $('#palette').hidden = true; },
  async query(text) {
    const q = text.trim();
    const seq = ++this.seq;
    const cmdOnly = q.startsWith('>');
    const cq = (cmdOnly ? q.slice(1) : q).trim().toLowerCase();
    const groups = [];
    const cmds = COMMANDS.filter(c => !cq || c.label.toLowerCase().includes(cq));
    if (!cmdOnly && q.length >= 2) {
      const [types, nodes] = await Promise.all([
        API.get('/api/types/search', { q, limit: 8 }).catch(() => []),
        API.get('/api/nodes/search', { q, limit: 8 }).catch(() => []),
      ]);
      if (seq !== this.seq) return;
      if (types.length) groups.push(['类型', types.map(t => ({ kind: t.kind, label: t.name, sub: `${t.ns || '(全局)'} · ${t.asm}`, run: () => openType(t.full, t.asm) }))]);
      if (nodes.length) groups.push(['GameObject', nodes.map(n => ({ icon: nodeIcon(n), label: n.name, sub: n.path, run: () => Inspector.open(n.h) }))]);
    }
    if (cmds.length) groups.push(['命令', cmds]);
    if (!cmdOnly && q) groups.push(['控制台', [{ icon: 'play', label: `执行：${q}`, run: () => { Console.set(q); Console.focus(); Console.run(); } }]]);
    if (seq !== this.seq) return;
    this.render(groups);
  },
  render(groups) {
    const list = $('#palette-list');
    this.items = [];
    list.replaceChildren();
    for (const [title, items] of groups) {
      list.append(h('div', { class: 'p-group', text: title }));
      for (const it of items) {
        const idx = this.items.length;
        const kindLetter = { class: 'C', struct: 'S', enum: 'E', interface: 'I', static: 'St', delegate: 'D' };
        const row = h('div', { class: 'p-item' },
          it.kind ? h('span', { class: 'kind k-' + it.kind, text: kindLetter[it.kind] || 'C' }) : icon(it.icon || 'right'),
          h('span', { class: 'pl', text: it.label }), it.sub ? h('span', { class: 'pd', text: it.sub }) : null);
        row.addEventListener('mousemove', () => { if (this.sel !== idx) { this.sel = idx; this.highlight(); } });
        row.addEventListener('click', () => { this.sel = idx; this.accept(); });
        list.append(row);
        this.items.push({ it, row });
      }
    }
    if (!this.items.length) list.append(h('div', { class: 'p-empty', text: '没有结果' }));
    this.sel = 0;
    this.highlight();
  },
  highlight() {
    this.items.forEach((x, i) => x.row.classList.toggle('active', i === this.sel));
    this.items[this.sel]?.row.scrollIntoView({ block: 'nearest' });
  },
  move(d) {
    if (!this.items.length) return;
    this.sel = (this.sel + d + this.items.length) % this.items.length;
    this.highlight();
  },
  accept() {
    const x = this.items[this.sel];
    if (!x) return;
    this.close();
    x.it.run();
  },
};

function showAbout() {
  const row = (k, v) => h('tr', {}, h('td', {}, h('code', { text: k })), h('td', { text: v }));
  Modal.open('使用说明', h('div', {},
    h('h3', { text: '对象与句柄' }),
    h('p', { text: '每个可展开的值都有一个句柄 $n。在控制台中 $n 会被替换成带真实类型的引用，因此可以直接写 $12.SetActive(false)、$12.transform.position；$_ 是上一次执行或方法调用的结果。类型的句柄代表类型本身，可写 $7.Instance 读静态成员。' }),
    h('p', { text: '成员左侧的橙点表示 Il2Cpp 字段（由 Il2CppInterop 以属性形式暴露），空心圆表示属性。双击值可编辑基础类型、枚举、向量和颜色；布尔值直接勾选。' }),
    h('h3', { text: '快捷键' }),
    h('table', {},
      row('Ctrl+K', '命令面板：搜索类型、GameObject、命令'),
      row('Enter', '控制台执行（代码不完整时换行）'),
      row('Shift+Enter', '换行'),
      row('Ctrl+Enter', '总是执行（程序模式下使用）'),
      row('Ctrl+Space', '代码补全（输入 . 时自动触发）'),
      row('↑ / ↓', '控制台历史'),
      row('Ctrl+L', '清空控制台输出'),
      row('Alt+← / Alt+→', '检查器后退 / 前进'),
      row('Ctrl+`', '最大化 / 还原底部面板')),
    h('h3', { text: '说明' }),
    h('p', { text: '所有读取游戏对象的操作都在 Unity 主线程执行；游戏在后台暂停或加载时请求会等待。类型浏览、补全和日志不依赖主线程。监视表达式独立编译，不能使用控制台变量，但可以使用静态成员和 $句柄。' }),
  ));
}

const Status = {
  timer: 0,
  sceneSig: '',
  init() {},
  start() {
    clearInterval(this.timer);
    this.tick();
    this.timer = setInterval(() => { if (!document.hidden) this.tick(); }, 2000);
  },
  async tick() {
    if (this.busy) return;
    this.busy = true;
    const conn = $('#st-conn');
    try {
      const s = await API.get('/api/status');
      conn.className = 'chip ok';
      conn.lastChild.textContent = s.focused || s.runInBackground ? '已连接' : '已连接 · 失焦';
      conn.title = `游戏 ${s.focused ? '在前台' : '在后台'}；后台运行 ${s.runInBackground ? '开启' : '关闭'}`;
      $('#st-fps').textContent = `${Math.round(s.fps)} fps`;
      $('#st-fps').title = `帧 ${s.frame} · timeScale ${s.timeScale}`;
      $('#st-scene').textContent = s.activeScene || '—';
      $('#st-scene').title = '已加载场景：' + s.scenes.join(', ');
      $('#st-mem').textContent = `${fmtMb(s.managedMb)} / ${fmtMb(s.workingSetMb)}`;
      $('#st-handles').textContent = `${s.handles} 句柄`;
      $('#game-title').textContent = `${s.product} ${s.version}`;
      document.title = `${s.product} · Il2Cpp Console`;
      if (Session.setBoot(s.boot)) {
        toast('检测到游戏已重启，旧句柄已失效');
        Inspector.history = [];
        Inspector.index = -1;
        Inspector.welcome();
      }
      const sig = s.scenes.join('|');
      if (this.sceneSig && sig !== this.sceneSig) SceneTree.load();
      this.sceneSig = sig;
      this.plugins = s.plugins;
    } catch (e) {
      if (e.code === 'main-thread-timeout') {
        conn.className = 'chip warn';
        conn.lastChild.textContent = '主线程无响应';
        conn.title = describeError(e);
      } else if (e.status !== 401) {
        conn.className = 'chip bad';
        conn.lastChild.textContent = '离线';
        conn.title = describeError(e);
      }
    } finally {
      this.busy = false;
    }
  },
};

const Auth = {
  ready: false,
  init() {
    $('#login-form').addEventListener('submit', async e => {
      e.preventDefault();
      const token = $('#login-token').value.trim();
      if (!token) return;
      await this.tryToken(token, $('#login-remember').checked);
    });
  },
  async boot() {
    const m = location.hash.match(/token=([^&]+)/);
    if (m) {
      const token = decodeURIComponent(m[1]);
      history.replaceState(null, '', location.pathname + location.search);
      return this.tryToken(token, false);
    }
    const token = store.get('token', null, 'session') || store.get('token');
    if (token) return this.tryToken(token, !!store.get('token'), true);
    this.showLogin();
  },
  async tryToken(token, remember, silent = false) {
    API.token = token;
    try {
      await API.call('/api/status', {});
      store.set('token', token, 'session');
      if (remember) store.set('token', token); else store.del('token');
      $('#login').hidden = true;
      $('#app').hidden = false;
      if (!this.ready) { this.ready = true; App.start(); }
    } catch (e) {
      if (e.status === 401) {
        store.del('token', 'session');
        store.del('token');
        this.showLogin(silent ? '' : 'token 不正确');
      } else if (e.code === 'main-thread-timeout') {
        // The token is fine; the game is just busy.
        store.set('token', token, 'session');
        $('#login').hidden = true;
        $('#app').hidden = false;
        if (!this.ready) { this.ready = true; App.start(); }
      } else {
        this.showLogin(describeError(e));
      }
    }
  },
  showLogin(error = '') {
    $('#login').hidden = false;
    const err = $('#login-error');
    err.hidden = !error;
    err.textContent = error;
    $('#login-token').focus();
  },
  unauthorized() {
    if (!$('#login').hidden) return;
    store.del('token', 'session');
    store.del('token');
    this.showLogin('连接已失效，请重新输入 token（游戏重启后 token 会变化）');
  },
  logout() {
    store.del('token', 'session');
    store.del('token');
    location.reload();
  },
};

const App = {
  start() {
    Status.start();
    Logs.start();
    SceneTree.load();
    if (Sidebar.current === 'session') Session.tickWatches();
  },
};

function keyboard() {
  addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); Palette.open(); return; }
    if (e.ctrlKey && e.key === '`') { e.preventDefault(); Drawer.toggleMax(); return; }
    if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); Inspector.go(Inspector.index - 1); return; }
    if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); Inspector.go(Inspector.index + 1); return; }
    if (e.key === 'Escape') { Menu.close(); if (!$('#modal').hidden) Modal.close(); }
  });
  addEventListener('mousedown', e => { if (!e.target.closest('#menu')) Menu.close(); });
  addEventListener('blur', () => Menu.close());
  $('#modal-close').addEventListener('click', () => Modal.close());
  $('#modal').addEventListener('mousedown', e => { if (e.target.id === 'modal') Modal.close(); });
  $('#about-open').addEventListener('click', () => showAbout());
}

Theme.init();
Mobile.init();
Layout.init();
Sidebar.init();
Drawer.init();
Inspector.init();
SceneTree.init();
Types.init();
Session.init();
Console.init();
Logs.init();
Palette.init();
Auth.init();
keyboard();
Auth.boot();
