"use strict";

/* ============================================================
   Portal Log Report - SPA client (vanilla JS)
   ============================================================ */

const State = {
  token: localStorage.getItem("plr_token") || null,
  user: null,
  catalog: [],
  view: "report",
  currentTemplate: null,   // TemplateDetail
  result: null,            // { columns, rows, ... } raw from server
  grid: { sortCol: -1, sortDir: 1, page: 1, pageSize: 50, filter: "" },
  pendingParams: null,     // params last used (for export / rerun)
  idleTimer: null,
  idleMinutes: 30
};

const $ = (sel, root = document) => root.querySelector(sel);
const el = (tag, attrs = {}, ...kids) => {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") n.className = v;
    else if (k === "html") n.innerHTML = v;
    else if (k.startsWith("on") && typeof v === "function") n.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined) n.setAttribute(k, v);
  }
  for (const kid of kids) if (kid != null) n.append(kid.nodeType ? kid : document.createTextNode(kid));
  return n;
};
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

/* ---------------- API ---------------- */
async function api(path, { method = "GET", body = null } = {}) {
  const headers = { "Content-Type": "application/json" };
  if (State.token) headers["Authorization"] = "Bearer " + State.token;
  const res = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : null });
  // 401 chỉ coi là "hết phiên" khi ĐANG đăng nhập (có token) và KHÔNG phải chính lời gọi login.
  // Lời gọi login trả 401 nghĩa là sai tài khoản/mật khẩu -> để thông báo của server hiển thị.
  if (res.status === 401 && State.token && !path.startsWith("/api/auth/login")) {
    doLogout(true);
    throw { message: "Phiên đăng nhập đã hết hạn." };
  }
  const ct = res.headers.get("content-type") || "";
  const data = ct.includes("application/json") ? await res.json() : null;
  if (res.status === 409 && data?.mustChangePassword) {
    if (State.user) State.user.mustChangePassword = true;
    forceChangePassword();
    throw data;
  }
  if (!res.ok) throw data || { message: "Lỗi " + res.status };
  return data;
}

async function apiBlob(path, body) {
  const headers = { "Content-Type": "application/json" };
  if (State.token) headers["Authorization"] = "Bearer " + State.token;
  const res = await fetch(path, { method: "POST", headers, body: JSON.stringify(body) });
  if (!res.ok) {
    let msg = "Lỗi " + res.status;
    try { msg = (await res.json()).message || msg; } catch {}
    throw { message: msg };
  }
  const blob = await res.blob();
  const cd = res.headers.get("content-disposition") || "";
  const m = cd.match(/filename="?([^"]+)"?/);
  return { blob, filename: m ? m[1] : "report.xlsx" };
}

/* ---------------- Auth ---------------- */
$("#loginForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const btn = $("#loginBtn");
  const errBox = $("#loginError");
  errBox.classList.add("hidden");
  btn.disabled = true; btn.textContent = "Đang đăng nhập…";
  try {
    const data = await api("/api/auth/login", {
      method: "POST",
      body: { username: $("#username").value.trim(), password: $("#password").value }
    });
    State.token = data.token;
    State.user = data.user;
    localStorage.setItem("plr_token", data.token);
    await startApp();
  } catch (ex) {
    errBox.textContent = ex.message || "Đăng nhập thất bại.";
    errBox.classList.remove("hidden");
  } finally {
    btn.disabled = false; btn.textContent = "Đăng nhập";
  }
});

$("#logoutBtn").addEventListener("click", () => doLogout(false));
$("#changePwdBtn").addEventListener("click", () => openChangePassword());

/* ---------------- Modal helper ---------------- */
function showModal(title, bodyNode, { wide = false, dismissable = true } = {}) {
  const backdrop = el("div", { class: "modal-backdrop" });
  const close = () => backdrop.remove();
  const head = el("div", { class: "modal-head" }, el("span", {}, title));
  if (dismissable) head.append(el("button", { class: "x", onclick: close }, "✕"));
  const modal = el("div", { class: "modal" + (wide ? " wide" : "") },
    head, el("div", { class: "modal-body" }, bodyNode));
  backdrop.append(modal);
  if (dismissable) backdrop.addEventListener("click", (e) => { if (e.target === backdrop) close(); });
  document.body.append(backdrop);
  return close;
}

let pwdModalOpen = false;
function openChangePassword({ forced = false, onDone = null } = {}) {
  if (pwdModalOpen) return;
  pwdModalOpen = true;
  const cur = el("input", { type: "password", autocomplete: "current-password" });
  const nw = el("input", { type: "password", autocomplete: "new-password" });
  const cf = el("input", { type: "password", autocomplete: "new-password" });
  const err = el("div", { class: "alert err hidden" });
  const body = el("div", {});
  if (forced)
    body.append(el("div", { class: "alert warn" },
      "⚠ Đây là lần đăng nhập đầu tiên (hoặc mật khẩu vừa được quản trị viên đặt lại). Vui lòng đặt mật khẩu riêng để tiếp tục."));
  body.append(field(forced ? "Mật khẩu tạm (được cấp)" : "Mật khẩu hiện tại", cur),
    field("Mật khẩu mới (≥ 6 ký tự)", nw), field("Nhập lại mật khẩu mới", cf), err);
  const save = el("button", { class: "btn primary" }, "Đổi mật khẩu");
  body.append(el("div", { class: "toolbar" }, save));
  const close = showModal(forced ? "Bắt buộc đổi mật khẩu" : "Đổi mật khẩu", body, { dismissable: !forced });
  const finish = () => { pwdModalOpen = false; close(); };
  save.addEventListener("click", async () => {
    err.classList.add("hidden");
    if (nw.value.length < 6) return showErr(err, "Mật khẩu mới phải có ít nhất 6 ký tự.");
    if (nw.value !== cf.value) return showErr(err, "Mật khẩu nhập lại không khớp.");
    save.disabled = true; save.innerHTML = '<span class="spinner"></span> Đang đổi…';
    try {
      const res = await api("/api/auth/change-password", { method: "POST", body: { currentPassword: cur.value, newPassword: nw.value } });
      // Nhận token mới (đã xóa cờ bắt buộc đổi).
      State.token = res.token; State.user = res.user;
      localStorage.setItem("plr_token", res.token);
      finish();
      toast("Đã đổi mật khẩu thành công.");
      if (onDone) await onDone();
    } catch (ex) { showErr(err, ex.message || "Đổi mật khẩu thất bại."); }
    finally { save.disabled = false; save.textContent = "Đổi mật khẩu"; }
  });
}
function forceChangePassword() { openChangePassword({ forced: true, onDone: continueApp }); }

function field(label, input) { return el("div", { class: "field" }, el("label", {}, label), input); }
function showErr(box, msg) { box.textContent = msg; box.classList.remove("hidden"); }

async function doLogout(expired) {
  try { if (!expired && State.token) await api("/api/auth/logout", { method: "POST" }); } catch {}
  State.token = null; State.user = null;
  localStorage.removeItem("plr_token");
  clearTimeout(State.idleTimer);
  $("#app").classList.remove("show");
  $("#login").classList.remove("hidden");
  if (expired) {
    const e = $("#loginError"); e.textContent = "Phiên đã hết hạn, vui lòng đăng nhập lại."; e.classList.remove("hidden");
  }
}

function has(perm) { return State.user?.permissions?.includes(perm); }

/* ---------------- App start ---------------- */
async function startApp() {
  $("#login").classList.add("hidden");
  $("#app").classList.add("show");
  $("#hdrName").textContent = State.user.fullName || State.user.username;
  $("#hdrDept").textContent = State.user.department ? "· " + State.user.department : "";
  resetIdle();
  if (!State.idleBound) {
    ["mousemove", "keydown", "click"].forEach(ev => document.addEventListener(ev, resetIdle));
    State.idleBound = true;
  }
  if (State.user.mustChangePassword) { forceChangePassword(); return; }
  await continueApp();
}

async function continueApp() {
  await loadCatalog();
  renderSidebar();
  navigate("report");
}

function resetIdle() {
  clearTimeout(State.idleTimer);
  State.idleTimer = setTimeout(() => doLogout(true), State.idleMinutes * 60 * 1000);
}

async function loadCatalog() {
  try { State.catalog = await api("/api/catalog"); }
  catch { State.catalog = []; }
}

/* ---------------- Sidebar ---------------- */
function renderSidebar() {
  const sb = $("#sidebar");
  sb.innerHTML = "";
  sb.append(navItem("📄 Báo cáo", "report"));
  sb.append(navItem("🕑 Lịch sử của tôi", "history"));
  if (has("USER.MANAGE") || has("CONNECTION.MANAGE") || has("TEMPLATE.MANAGE") || has("AUDIT.VIEW"))
    sb.append(navItem("⚙️ Quản trị", "admin"));

  if (State.view === "report") {
    const q = (State.catalogFilter || "").trim().toLowerCase();
    const search = el("input", { class: "search", placeholder: "🔍 Tìm báo cáo…", value: State.catalogFilter || "" });
    search.addEventListener("input", () => { State.catalogFilter = search.value; renderSidebar(); search.focus(); });
    sb.append(el("div", { class: "side-search" }, search));

    let shown = 0;
    for (const cat of State.catalog) {
      const matches = cat.templates.filter(t => !q || t.templateName.toLowerCase().includes(q) || t.templateCode.toLowerCase().includes(q));
      if (matches.length === 0) continue;
      sb.append(el("div", { class: "cat" }, cat.categoryName));
      for (const t of matches) {
        shown++;
        sb.append(el("div", {
          class: "tpl" + (State.currentTemplate?.templateId === t.templateId ? " active" : ""),
          onclick: () => openTemplate(t.templateId)
        }, t.templateName));
      }
    }
    if (State.catalog.length === 0)
      sb.append(el("div", { class: "tpl muted" }, "Chưa có báo cáo được cấp quyền."));
    else if (shown === 0)
      sb.append(el("div", { class: "tpl muted" }, "Không có báo cáo khớp từ khóa."));
  }
}

function navItem(label, view) {
  return el("div", {
    class: "nav-item" + (State.view === view ? " active" : ""),
    onclick: () => navigate(view)
  }, label);
}

function navigate(view) {
  State.view = view;
  renderSidebar();
  if (view === "report") renderReportHome();
  else if (view === "history") renderHistory();
  else if (view === "admin") renderAdmin();
}

/* ---------------- Report ---------------- */
function renderReportHome() {
  const c = $("#content");
  if (State.currentTemplate) return renderTemplate(State.currentTemplate);
  c.innerHTML = "";
  c.append(el("h2", { class: "page-title" }, `Xin chào, ${esc(State.user.fullName || State.user.username)} 👋`));
  c.append(el("p", { class: "page-sub" }, "Chọn một báo cáo để nhập tham số và chạy."));

  const total = State.catalog.reduce((s, x) => s + x.templates.length, 0);
  const exportable = State.catalog.reduce((s, x) => s + x.templates.filter(t => t.allowExport).length, 0);
  const kpis = el("div", { class: "kpi-grid" });
  kpis.append(kpiCard("📄", "Báo cáo được cấp quyền", total));
  kpis.append(kpiCard("🗂️", "Nhóm báo cáo", State.catalog.length));
  kpis.append(kpiCard("⬇️", "Báo cáo cho phép xuất Excel", exportable));
  kpis.append(kpiCard("🛡️", "Vai trò của bạn", (State.user.roles || []).length));
  c.append(kpis);

  if (total === 0) {
    c.append(emptyState("🔒", "Bạn chưa được cấp quyền báo cáo nào", "Liên hệ quản trị viên để được phân quyền."));
    return;
  }
  for (const cat of State.catalog) {
    const panel = el("div", { class: "panel" });
    panel.append(el("h3", {}, cat.categoryName + ` (${cat.templates.length})`));
    const grid = el("div", { class: "form-grid" });
    for (const t of cat.templates) {
      const card = el("div", {
        class: "field", style: "cursor:pointer;border:1px solid var(--border);border-radius:9px;padding:12px 14px;transition:box-shadow .15s,border-color .15s",
        onclick: () => openTemplate(t.templateId),
        onmouseover: function () { this.style.boxShadow = "var(--shadow-md)"; this.style.borderColor = "var(--secondary)"; },
        onmouseout: function () { this.style.boxShadow = ""; this.style.borderColor = "var(--border)"; }
      },
        el("div", { style: "font-weight:700;color:var(--heading)" }, t.templateName),
        el("div", { class: "muted", style: "font-size:12.5px;margin-top:3px" }, t.description || t.templateCode),
        el("div", { style: "margin-top:8px;display:flex;gap:6px;flex-wrap:wrap" },
          el("span", { class: "badge info" }, `${t.parameterCount} tham số`),
          t.allowExport ? el("span", { class: "badge ok" }, "Excel") : null,
          t.allowCopy ? el("span", { class: "badge mut" }, "Copy") : null));
      grid.append(card);
    }
    panel.append(grid);
    c.append(panel);
  }
}

function kpiCard(icon, label, value) {
  return el("div", { class: "kpi" },
    el("span", { class: "icon" }, icon),
    el("div", { class: "label" }, label),
    el("div", { class: "value" }, Number(value).toLocaleString("vi-VN")));
}

function emptyState(ico, msg, hint) {
  return el("div", { class: "empty" },
    el("div", { class: "ico" }, ico),
    el("div", { class: "msg" }, msg),
    hint ? el("div", { class: "hint" }, hint) : null);
}

async function openTemplate(id) {
  const c = $("#content");
  c.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải báo cáo…</p>';
  try {
    State.currentTemplate = await api("/api/catalog/templates/" + id);
    State.result = null; State.pendingParams = null;
    renderSidebar();
    renderTemplate(State.currentTemplate);
  } catch (ex) {
    c.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`;
  }
}

function renderTemplate(t, prefill = null) {
  const c = $("#content");
  c.innerHTML = "";
  c.append(el("h2", { class: "page-title" }, t.templateName));
  c.append(el("p", { class: "page-sub" }, t.description || t.templateCode));

  const form = el("form", { class: "panel", id: "paramForm" });
  form.append(el("h3", {}, "Tham số đầu vào"));
  if (t.parameters.length === 0) {
    form.append(el("p", { class: "muted" }, "Báo cáo này không cần tham số."));
  } else {
    const grid = el("div", { class: "form-grid" });
    for (const p of t.parameters) grid.append(buildField(p, prefill));
    form.append(grid);
  }
  const errBox = el("div", { class: "alert err hidden", id: "runError" });
  form.append(errBox);

  const runBtn = el("button", { class: "btn primary", type: "submit", id: "runBtn" }, "▶ Chạy báo cáo");
  const bar = el("div", { class: "toolbar" }, runBtn);
  form.append(bar);
  form.addEventListener("submit", (e) => { e.preventDefault(); runReport(t); });
  c.append(form);

  c.append(el("div", { id: "resultArea" }));
  if (State.result) renderResult();
}

function buildField(p, prefill) {
  const val = prefill && prefill[p.parameterName] != null ? prefill[p.parameterName] : (p.defaultValue ?? "");
  const label = el("label", {}, p.displayName, p.isRequired ? el("span", { class: "req" }, " *") : "");
  let input;
  const type = p.inputType;
  if (p.dataType === "Dropdown" && p.options) {
    input = el("select", { name: p.parameterName });
    input.append(el("option", { value: "" }, "-- chọn --"));
    try {
      JSON.parse(p.options).forEach(o => {
        const value = typeof o === "object" ? o.value : o;
        const text = typeof o === "object" ? (o.label ?? o.value) : o;
        input.append(el("option", { value, ...(String(value) === String(val) ? { selected: "" } : {}) }, text));
      });
    } catch {}
  } else {
    const htmlType = ({ Date: "date", DateTime: "datetime-local", Number: "number" })[type] || "text";
    input = el("input", { name: p.parameterName, type: htmlType, value: val });
    if (p.maxLength) input.setAttribute("maxlength", p.maxLength);
    if (p.minValue && (p.dataType === "Integer" || p.dataType === "Decimal")) input.setAttribute("min", p.minValue);
    if (p.maxValue && (p.dataType === "Integer" || p.dataType === "Decimal")) input.setAttribute("max", p.maxValue);
    if (p.dataType === "Decimal") input.setAttribute("step", "any");
  }
  return el("div", { class: "field" }, label, input);
}

function collectParams() {
  const params = {};
  document.querySelectorAll("#paramForm [name]").forEach(inp => {
    let v = inp.value;
    // datetime-local -> "yyyy-MM-ddTHH:mm" is fine for the backend parser
    params[inp.name] = v === "" ? null : v;
  });
  return params;
}

async function runReport(t) {
  const btn = $("#runBtn"); const errBox = $("#runError");
  errBox.classList.add("hidden");
  btn.disabled = true; btn.innerHTML = '<span class="spinner"></span> Đang chạy…';
  const params = collectParams();
  State.pendingParams = params;
  try {
    const r = await api(`/api/reports/${t.templateId}/run`, { method: "POST", body: { parameters: params } });
    State.result = r;
    State.grid = { sortCol: -1, sortDir: 1, page: 1, pageSize: 50, filter: "", selected: new Set() };
    renderResult();
  } catch (ex) {
    errBox.textContent = ex.message || "Chạy báo cáo thất bại.";
    errBox.classList.remove("hidden");
  } finally {
    btn.disabled = false; btn.innerHTML = "▶ Chạy báo cáo";
  }
}

/* ---------------- Result grid ---------------- */
function renderResult() {
  const area = $("#resultArea");
  const r = State.result;
  area.innerHTML = "";
  const t = State.currentTemplate;

  area.append(el("h3", { class: "page-title", style: "font-size:16px;margin:4px 0 2px" },
    el("span", { class: "badge ok" }, "✓ Thành công"), " Kết quả"));

  const info = el("span", { class: "info" },
    `${r.rowCount.toLocaleString("vi-VN")} dòng · ${r.durationMs} ms` + (r.truncated ? ` · đã cắt bớt ở ${t.maxRows.toLocaleString("vi-VN")} dòng` : ""));
  const search = el("input", { class: "search", placeholder: "🔍 Tìm nhanh trong kết quả…", value: State.grid.filter });
  search.addEventListener("input", () => { State.grid.filter = search.value; State.grid.page = 1; drawTable(); });

  const sizeSel = el("select", { title: "Số dòng mỗi trang" });
  [25, 50, 100, 200].forEach(n => sizeSel.append(el("option", { value: n, ...(n === State.grid.pageSize ? { selected: "" } : {}) }, n + "/trang")));
  sizeSel.addEventListener("change", () => { State.grid.pageSize = parseInt(sizeSel.value, 10); State.grid.page = 1; drawTable(); });

  const bar = el("div", { class: "toolbar" }, info, el("span", { class: "spacer", style: "flex:1" }), search, sizeSel);
  if (r.allowCopy) {
    bar.append(el("button", { class: "btn ghost sm", id: "copySelBtn", onclick: copySelected }, "📋 Copy dòng chọn"));
    bar.append(el("button", { class: "btn ghost sm", onclick: copyResult }, "📋 Copy tất cả"));
  }
  if (r.allowExport && t.allowExport) bar.append(el("button", { class: "btn primary sm", id: "exportBtn", onclick: () => exportReport(t) }, "⬇ Xuất Excel"));
  area.append(bar);

  if (r.allowCopy) area.append(el("div", { class: "muted", style: "font-size:12px;margin:-6px 0 8px" },
    "Mẹo: tích ô để chọn dòng · nhấp đúp vào một ô để copy ô đó."));
  if (r.truncated) area.append(el("div", { class: "alert warn" }, `Kết quả bị giới hạn ${t.maxRows.toLocaleString("vi-VN")} dòng. Hãy thu hẹp tham số để lấy đủ dữ liệu.`));

  if (r.rowCount === 0) {
    area.append(emptyState("📭", "Truy vấn không trả về dòng nào", "Thử điều chỉnh lại tham số lọc."));
    return;
  }
  area.append(el("div", { class: "table-wrap", id: "tableWrap" }));
  area.append(el("div", { class: "pager", id: "pager" }));
  drawTable();
}

function filteredRows() {
  const r = State.result;
  let rows = r.rows;
  const f = State.grid.filter.trim().toLowerCase();
  if (f) rows = rows.filter(row => row.some(c => String(c ?? "").toLowerCase().includes(f)));
  if (State.grid.sortCol >= 0) {
    const col = State.grid.sortCol, dir = State.grid.sortDir;
    rows = [...rows].sort((a, b) => cmp(a[col], b[col]) * dir);
  }
  return rows;
}

function cmp(a, b) {
  if (a == null && b == null) return 0;
  if (a == null) return -1;
  if (b == null) return 1;
  const na = Number(a), nb = Number(b);
  if (!isNaN(na) && !isNaN(nb)) return na - nb;
  return String(a).localeCompare(String(b), "vi");
}

function drawTable() {
  const r = State.result;
  const wrap = $("#tableWrap");
  const rows = filteredRows();
  const { page, pageSize } = State.grid;
  const start = (page - 1) * pageSize;
  const pageRows = rows.slice(start, start + pageSize);

  const sel = State.grid.selected;
  const table = el("table");
  const thead = el("tr");
  // header checkbox: select/deselect all rows on current page
  const allOnPage = pageRows.length > 0 && pageRows.every(row => sel.has(row));
  const headChk = el("input", { type: "checkbox", ...(allOnPage ? { checked: "" } : {}) });
  headChk.addEventListener("change", () => {
    pageRows.forEach(row => headChk.checked ? sel.add(row) : sel.delete(row));
    drawTable();
  });
  thead.append(el("th", { class: "chk nosort" }, headChk));
  r.columns.forEach((c, i) => {
    const arrow = State.grid.sortCol === i ? (State.grid.sortDir === 1 ? " ▲" : " ▼") : "";
    thead.append(el("th", { onclick: () => sortBy(i) }, c + arrow));
  });
  table.append(el("thead", {}, thead));

  const tbody = el("tbody");
  for (const row of pageRows) {
    const tr = el("tr", { class: sel.has(row) ? "sel" : "" });
    const rowChk = el("input", { type: "checkbox", ...(sel.has(row) ? { checked: "" } : {}) });
    rowChk.addEventListener("change", () => { rowChk.checked ? sel.add(row) : sel.delete(row); tr.classList.toggle("sel", rowChk.checked); updateCopySelBtn(); });
    tr.append(el("td", { class: "chk" }, rowChk));
    row.forEach(cell => {
      const isNum = typeof cell === "number";
      const text = cell == null ? "" : String(cell);
      const td = el("td", { class: (isNum ? "num " : "") + "cell-copy", title: "Nhấp đúp để copy ô" }, text);
      td.addEventListener("dblclick", async () => {
        if (!r.allowCopy) return;
        try { await navigator.clipboard.writeText(text); toast("Đã copy ô: " + (text.length > 40 ? text.slice(0, 40) + "…" : text)); }
        catch { toast("Trình duyệt chặn clipboard.", "warn"); }
      });
      tr.append(td);
    });
    tbody.append(tr);
  }
  if (pageRows.length === 0)
    tbody.append(el("tr", {}, el("td", { colspan: r.columns.length + 1, class: "muted", style: "text-align:center;padding:20px" }, "Không có dòng khớp từ khóa tìm nhanh.")));
  table.append(tbody);
  wrap.innerHTML = ""; wrap.append(table);
  updateCopySelBtn();

  // pager
  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  const pager = $("#pager");
  pager.innerHTML = "";
  pager.append(el("button", { disabled: page <= 1 ? "" : null, onclick: () => { State.grid.page--; drawTable(); } }, "‹ Trước"));
  pager.append(el("span", { class: "info" }, ` Trang ${page}/${totalPages} (${rows.length.toLocaleString("vi-VN")} dòng) `));
  pager.append(el("button", { disabled: page >= totalPages ? "" : null, onclick: () => { State.grid.page++; drawTable(); } }, "Sau ›"));
}

function sortBy(i) {
  if (State.grid.sortCol === i) State.grid.sortDir *= -1;
  else { State.grid.sortCol = i; State.grid.sortDir = 1; }
  drawTable();
}

function updateCopySelBtn() {
  const btn = $("#copySelBtn");
  if (!btn) return;
  const n = State.grid.selected?.size || 0;
  btn.textContent = n ? `📋 Copy ${n} dòng chọn` : "📋 Copy dòng chọn";
  btn.disabled = n === 0;
}

function rowsToTsv(rows) {
  const r = State.result;
  const lines = [r.columns.join("\t")];
  for (const row of rows) lines.push(row.map(c => c == null ? "" : String(c)).join("\t"));
  return lines.join("\n");
}

async function clipboard(text, okMsg) {
  try { await navigator.clipboard.writeText(text); toast(okMsg); }
  catch { toast("Trình duyệt chặn clipboard. Hãy bôi đen bảng để sao chép thủ công.", "warn"); }
}

async function copyResult() {
  const rows = filteredRows();
  await clipboard(rowsToTsv(rows), `Đã copy toàn bộ ${rows.length.toLocaleString("vi-VN")} dòng.`);
}

async function copySelected() {
  // Giữ nguyên thứ tự hiển thị hiện tại.
  const rows = filteredRows().filter(row => State.grid.selected.has(row));
  if (rows.length === 0) return toast("Chưa chọn dòng nào.", "warn");
  await clipboard(rowsToTsv(rows), `Đã copy ${rows.length} dòng được chọn.`);
}

async function exportReport(t) {
  const btn = $("#exportBtn");
  btn.disabled = true; btn.innerHTML = '<span class="spinner"></span> Đang xuất…';
  try {
    const { blob, filename } = await apiBlob(`/api/reports/${t.templateId}/export`, { parameters: State.pendingParams || collectParams() });
    const url = URL.createObjectURL(blob);
    const a = el("a", { href: url, download: filename });
    document.body.append(a); a.click(); a.remove();
    URL.revokeObjectURL(url);
    toast("Đã xuất file " + filename);
  } catch (ex) {
    toast(ex.message || "Xuất Excel thất bại.", "err");
  } finally {
    btn.disabled = false; btn.innerHTML = "⬇ Xuất Excel";
  }
}

/* ---------------- History ---------------- */
async function renderHistory() {
  State.currentTemplate = null;
  const c = $("#content");
  c.innerHTML = "";
  c.append(el("h2", { class: "page-title" }, "Lịch sử truy vấn"));
  c.append(el("p", { class: "page-sub" }, "Các lần chạy báo cáo gần đây."));

  const showAll = has("HISTORY.VIEW.ALL");
  if (showAll) {
    const tabs = el("div", { class: "tabs" });
    const defs = [["Của tôi", () => loadHistory("my")], ["Toàn hệ thống", () => loadHistory("all")], ["📊 Thống kê sử dụng", loadTopUsers]];
    defs.forEach(([name, fn], i) => {
      const t = el("div", { class: "tab" + (i === 0 ? " active" : "") }, name);
      t.onclick = () => { tabs.querySelectorAll(".tab").forEach(x => x.classList.remove("active")); t.classList.add("active"); fn(); };
      tabs.append(t);
    });
    c.append(tabs);
  }
  c.append(el("div", { id: "histArea" }));
  loadHistory("my");
}

// bộ lọc cho tab "Toàn hệ thống" (giữ giữa các lần tải lại)
const HistFilter = { q: "", status: "all", sort: "started_desc", from: "", to: "" };

let histReqSeq = 0; // chống race: chỉ nhận kết quả của lần gọi mới nhất

async function loadHistory(which) {
  const area = $("#histArea");
  const all = which === "all";
  area.innerHTML = "";

  // Phần bảng được nạp lại độc lập — KHÔNG dựng lại thanh lọc (giữ focus ô đang gõ).
  const tableHolder = el("div", {});

  async function refresh() {
    const seq = ++histReqSeq;
    tableHolder.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
    try {
      let qs = "";
      if (all) {
        const p = new URLSearchParams({ q: HistFilter.q, status: HistFilter.status, sort: HistFilter.sort });
        // ngày người dùng chọn là ngày theo múi giờ máy -> quy về mốc UTC cho server
        if (HistFilter.from) p.set("from", new Date(HistFilter.from + "T00:00:00").toISOString());
        if (HistFilter.to) { const d = new Date(HistFilter.to + "T00:00:00"); d.setDate(d.getDate() + 1); p.set("to", d.toISOString()); }
        qs = "?" + p.toString();
      }
      const items = await api("/api/history/" + which + qs);
      if (seq !== histReqSeq) return; // đã có request mới hơn -> bỏ kết quả cũ
      const table = el("table");
      table.append(el("thead", {}, rowH(["#", "Báo cáo", "Người chạy", "Thời điểm", "Thời gian", "Số dòng", "Trạng thái", ""])));
      const tb = el("tbody");
      for (const h of items) {
        const rerun = el("button", { class: "btn ghost sm", onclick: () => rerunFromHistory(h.executionId) }, "Chạy lại");
        tb.append(el("tr", {},
          td(h.executionId), td(h.templateName), td(h.username),
          td(new Date(h.startedAt).toLocaleString("vi-VN")),
          tdNum(h.durationMs != null ? h.durationMs + " ms" : ""),
          tdNum(h.rowCountOut != null ? h.rowCountOut.toLocaleString("vi-VN") : ""),
          el("td", {}, statusBadge(h.status)), el("td", {}, rerun)));
      }
      if (items.length === 0) tb.append(el("tr", {}, el("td", { colspan: 8, class: "muted" }, "Không có lịch sử khớp bộ lọc.")));
      table.append(tb);
      tableHolder.innerHTML = ""; tableHolder.append(el("div", { class: "table-wrap" }, table));
    } catch (ex) {
      if (seq !== histReqSeq) return;
      tableHolder.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`;
    }
  }

  // thanh lọc chỉ cho lịch sử toàn hệ thống — dựng MỘT LẦN
  if (all) {
    const fQ = el("input", { type: "search", placeholder: "🔍 Tìm theo người chạy…", value: HistFilter.q, style: "min-width:200px" });
    const fStatus = el("select", {},
      ...[["all", "Tất cả trạng thái"], ["Success", "Thành công"], ["Failed", "Lỗi"], ["Timeout", "Timeout"], ["Cancelled", "Đã hủy"]]
        .map(([v, l]) => el("option", { value: v, ...(HistFilter.status === v ? { selected: "" } : {}) }, l)));
    const fSort = el("select", {},
      ...[["started_desc", "Mới nhất trước"], ["started_asc", "Cũ nhất trước"], ["duration_desc", "Chạy lâu nhất"], ["duration_asc", "Chạy nhanh nhất"]]
        .map(([v, l]) => el("option", { value: v, ...(HistFilter.sort === v ? { selected: "" } : {}) }, l)));
    const fFrom = el("input", { type: "date", value: HistFilter.from, title: "Từ ngày" });
    const fTo = el("input", { type: "date", value: HistFilter.to, title: "Đến ngày" });
    const fClear = el("button", { class: "btn ghost sm", type: "button", title: "Xóa bộ lọc" }, "✕ Xóa lọc");
    let deb = null;
    const apply = () => {
      HistFilter.q = fQ.value.trim(); HistFilter.status = fStatus.value; HistFilter.sort = fSort.value;
      HistFilter.from = fFrom.value; HistFilter.to = fTo.value;
      if (HistFilter.from && HistFilter.to && HistFilter.from > HistFilter.to)
        return toast('"Từ ngày" không được lớn hơn "Đến ngày".', "warn");
      refresh();                       // chỉ nạp lại bảng, không đụng thanh lọc
    };
    fQ.oninput = () => { clearTimeout(deb); deb = setTimeout(apply, 350); };
    fStatus.onchange = apply; fSort.onchange = apply; fFrom.onchange = apply; fTo.onchange = apply;
    fClear.onclick = () => {
      Object.assign(HistFilter, { q: "", status: "all", sort: "started_desc", from: "", to: "" });
      fQ.value = ""; fStatus.value = "all"; fSort.value = "started_desc"; fFrom.value = ""; fTo.value = "";
      refresh();
    };
    area.append(el("div", { class: "toolbar" },
      fQ, fStatus, fSort,
      el("span", { class: "info" }, "Từ"), fFrom, el("span", { class: "info" }, "đến"), fTo, fClear));
  }

  area.append(tableHolder);
  refresh();
}

async function loadTopUsers() {
  const area = $("#histArea");
  area.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
  try {
    const rows = await api("/api/history/stats/top-users");
    const table = el("table");
    table.append(el("thead", {}, rowH(["#", "Người dùng", "Phòng ban", "Tổng lượt chạy", "Thành công", "Lỗi", "Gần nhất"])));
    const tb = el("tbody");
    rows.forEach((r, i) => {
      const rank = i < 3 ? ["🥇", "🥈", "🥉"][i] : String(i + 1);
      const tr = el("tr", { class: "clickable", title: "Bấm để xem báo cáo người này đã chạy",
        onclick: () => openUserHistory(r) },
        el("td", { class: "num" }, rank),
        td((r.fullName ? r.fullName + " " : "") + "(" + r.username + ")"),
        td(r.department || "—"),
        tdNum(r.totalRuns.toLocaleString("vi-VN")),
        tdNum(r.successRuns.toLocaleString("vi-VN")),
        tdNum(r.failedRuns.toLocaleString("vi-VN")),
        td(r.lastRun ? new Date(r.lastRun).toLocaleString("vi-VN") : "—"));
      tb.append(tr);
    });
    if (rows.length === 0) tb.append(el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Chưa có lượt chạy nào.")));
    table.append(tb);
    area.innerHTML = "";
    area.append(el("p", { class: "page-sub" }, "Xếp hạng theo tổng số lần chạy báo cáo (nhiều nhất trước). Bấm vào một người để xem các báo cáo họ đã chạy."),
      el("div", { class: "table-wrap" }, table));
  } catch (ex) { area.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; }
}

/* Modal: các báo cáo một người dùng đã chạy (mở từ bảng thống kê). */
async function openUserHistory(r) {
  const label = (r.fullName ? r.fullName + " " : "") + "(" + r.username + ")";
  const bodyWrap = el("div", {}, el("p", { class: "muted" }, el("span", { class: "spinner" }), " Đang tải…"));
  showModal("📄 Báo cáo đã chạy — " + label, bodyWrap, { wide: true });
  let items;
  try { items = await api(`/api/history/user/${r.userId}`); }
  catch (ex) { bodyWrap.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; return; }

  // Tóm tắt theo từng báo cáo (số lần chạy), nhiều nhất trước.
  const byTpl = new Map();
  for (const h of items) {
    const k = h.templateName;
    byTpl.set(k, (byTpl.get(k) || 0) + 1);
  }
  const summary = el("div", { class: "perm-children", style: "grid-template-columns:1fr 1fr" });
  [...byTpl.entries()].sort((a, b) => b[1] - a[1]).forEach(([name, cnt]) =>
    summary.append(el("div", { class: "perm-item" },
      el("span", {}, name), el("span", { class: "badge mut", style: "margin-left:auto" }, cnt + " lần"))));

  // Danh sách chi tiết các lần chạy.
  const table = el("table");
  table.append(el("thead", {}, rowH(["#", "Báo cáo", "Thời điểm", "Thời gian", "Số dòng", "Trạng thái"])));
  const tb = el("tbody");
  for (const h of items) {
    tb.append(el("tr", {},
      td(h.executionId), td(h.templateName),
      td(new Date(h.startedAt).toLocaleString("vi-VN")),
      tdNum(h.durationMs != null ? h.durationMs + " ms" : ""),
      tdNum(h.rowCountOut != null ? h.rowCountOut.toLocaleString("vi-VN") : ""),
      el("td", {}, statusBadge(h.status))));
  }
  if (items.length === 0) tb.append(el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Người này chưa chạy báo cáo nào.")));
  table.append(tb);

  bodyWrap.innerHTML = "";
  if (byTpl.size > 0) {
    bodyWrap.append(el("h3", { style: "margin:0 0 8px" }, `Các báo cáo đã dùng (${byTpl.size})`), summary,
      el("h3", { style: "margin:16px 0 8px" }, `Chi tiết lần chạy (${items.length})`));
  }
  bodyWrap.append(el("div", { class: "table-wrap" }, table));
}

async function rerunFromHistory(execId) {
  try {
    const info = await api(`/api/history/${execId}/rerun`);
    State.view = "report";
    const detail = await api("/api/catalog/templates/" + info.templateId);
    State.currentTemplate = detail; State.result = null;
    renderSidebar();
    renderTemplate(detail, info.parameters);
    window.scrollTo(0, 0);
  } catch (ex) { toast(ex.message || "Không thể chạy lại.", "err"); }
}

function statusBadge(s) {
  const map = { Success: "ok", Failed: "err", Timeout: "warn", Running: "mut", Cancelled: "mut" };
  const label = { Success: "Thành công", Failed: "Lỗi", Timeout: "Timeout", Running: "Đang chạy", Cancelled: "Đã hủy" };
  return el("span", { class: "badge " + (map[s] || "mut") }, label[s] || s);
}

/* ---------------- Admin ---------------- */
function renderAdmin() {
  State.currentTemplate = null;
  const c = $("#content");
  c.innerHTML = "";
  c.append(el("h2", { class: "page-title" }, "Quản trị hệ thống"));
  const tabsDef = [];
  if (has("USER.MANAGE")) tabsDef.push(["Người dùng", adminUsers]);
  if (has("CONNECTION.MANAGE")) tabsDef.push(["Kết nối DB", adminConnections]);
  if (has("TEMPLATE.MANAGE")) tabsDef.push(["SQL Template", adminTemplates]);
  if (has("AUDIT.VIEW")) tabsDef.push(["Audit Log", adminAudit]);

  const tabs = el("div", { class: "tabs" });
  const body = el("div", { id: "adminBody" });
  tabsDef.forEach(([name, fn], i) => {
    const t = el("div", { class: "tab" + (i === 0 ? " active" : "") }, name);
    t.onclick = () => { tabs.querySelectorAll(".tab").forEach(x => x.classList.remove("active")); t.classList.add("active"); fn(body); };
    tabs.append(t);
  });
  c.append(tabs, body);
  if (tabsDef.length) tabsDef[0][1](body);
}

async function adminUsers(body) {
  body.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
  try {
    const users = await api("/api/admin/users");

    // ---- bộ lọc (phòng ban / vai trò / tìm tên) ----
    const depts = [...new Set(users.map(u => u.department).filter(Boolean))].sort((a, b) => a.localeCompare(b, "vi"));
    const roles = [...new Set(users.flatMap(u => u.roles || []))].sort((a, b) => a.localeCompare(b, "vi"));
    const fDept = el("select", {}, el("option", { value: "" }, "Tất cả phòng ban"),
      ...depts.map(d => el("option", { value: d }, d)));
    const fRole = el("select", {}, el("option", { value: "" }, "Tất cả vai trò"),
      ...roles.map(r => el("option", { value: r }, r)));
    const fSearch = el("input", { type: "search", placeholder: "🔍 Tìm tên đăng nhập / họ tên…", style: "min-width:220px" });

    const countInfo = el("span", { class: "info" });
    const bar = el("div", { class: "toolbar" },
      el("button", { class: "btn primary", onclick: () => openCreateUser(body) }, "➕ Thêm mới tài khoản"),
      el("button", { class: "btn ghost", onclick: () => openBulkImport(body) }, "⬆ Nhập hàng loạt (CSV)"),
      el("button", { class: "btn ghost", onclick: openOutbox }, "📧 Hộp thư (demo)"),
      fDept, fRole, fSearch, countInfo);

    const table = el("table");
    table.append(el("thead", {}, rowH(["Tên ĐN", "Họ tên", "Phòng ban", "Vai trò", "Trạng thái", "Đăng nhập cuối", "Thao tác"])));
    const tb = el("tbody");
    table.append(tb);

    const renderRows = () => {
      const dq = fDept.value, rq = fRole.value, sq = fSearch.value.trim().toLowerCase();
      tb.innerHTML = "";
      const shown = users.filter(u =>
        (!dq || u.department === dq) &&
        (!rq || (u.roles || []).includes(rq)) &&
        (!sq || (u.username + " " + (u.fullName || "")).toLowerCase().includes(sq)));
      for (const u of shown) {
        const toggle = el("button", { class: "btn ghost sm", onclick: () => setActive(u.userId, !u.isActive) }, u.isActive ? "Khóa" : "Mở khóa");
        const reset = el("button", { class: "btn ghost sm", onclick: () => resetPwd(u.userId, u.username) }, "Đổi MK");
        const perm = el("button", { class: "btn ghost sm", onclick: () => openUserPermissions(u) }, "🔑 Phân quyền");
        tb.append(el("tr", {},
          td(u.username), td(u.fullName || ""), td(u.department || ""),
          td((u.roles || []).join(", ")),
          el("td", {}, el("span", { class: "badge " + (u.isActive ? "ok" : "err") }, u.isActive ? "Hoạt động" : "Khóa")),
          td(u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString("vi-VN") : "—"),
          el("td", {}, perm, " ", toggle, " ", reset)));
      }
      if (shown.length === 0) tb.append(el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Không có tài khoản khớp bộ lọc.")));
      countInfo.textContent = `${shown.length}/${users.length} tài khoản`;
    };
    fDept.onchange = renderRows; fRole.onchange = renderRows; fSearch.oninput = renderRows;

    body.innerHTML = "";
    body.append(bar, el("div", { class: "table-wrap" }, table));
    renderRows();
  } catch (ex) { body.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; }
}

/* Modal gán quyền xem báo cáo cho từng người dùng (có checkbox theo nhóm). */
async function openUserPermissions(u) {
  let items;
  try { items = await api(`/api/admin/users/${u.userId}/permissions`); }
  catch (ex) { return toast(ex.message, "err"); }

  const wrap = el("div", { class: "perm-wrap" });
  wrap.append(el("p", { class: "muted", style: "font-size:13px" },
    `Chọn các báo cáo mà `, el("b", {}, u.fullName || u.username),
    ` được phép xem. Báo cáo đã được cấp qua vai trò hiển thị mờ (không bỏ được ở đây).`));

  // gom theo nhóm
  const groups = new Map();
  for (const it of items) {
    if (!groups.has(it.categoryId)) groups.set(it.categoryId, { name: it.categoryName, items: [] });
    groups.get(it.categoryId).items.push(it);
  }

  const allBoxes = [];
  for (const { name, items: gitems } of groups.values()) {
    const groupCb = el("input", { type: "checkbox", class: "perm-group-cb" });
    const childBoxes = [];
    const childWrap = el("div", { class: "perm-children" });
    for (const it of gitems) {
      const viaRole = it.grantedViaRole;
      const cb = el("input", {
        type: "checkbox", value: it.templateId, "data-perm": "1",
        ...(it.grantedDirect || viaRole ? { checked: "" } : {}),
        ...(viaRole ? { disabled: "" } : {})
      });
      cb.dataset.viaRole = viaRole ? "1" : "0";
      if (!viaRole) { childBoxes.push(cb); allBoxes.push(cb); }
      childWrap.append(el("label", { class: "perm-item" }, cb,
        el("span", {}, it.templateName),
        viaRole ? el("span", { class: "badge mut", style: "margin-left:6px" }, "qua vai trò") : null));
    }
    const syncGroup = () => {
      const on = childBoxes.filter(c => c.checked).length;
      groupCb.checked = childBoxes.length > 0 && on === childBoxes.length;
      groupCb.indeterminate = on > 0 && on < childBoxes.length;
    };
    groupCb.onchange = () => { childBoxes.forEach(c => c.checked = groupCb.checked); };
    childBoxes.forEach(c => c.addEventListener("change", syncGroup));
    syncGroup();
    wrap.append(el("div", { class: "perm-group" },
      el("label", { class: "perm-group-head" }, groupCb, el("b", {}, name)), childWrap));
  }
  if (groups.size === 0) wrap.append(el("div", { class: "muted" }, "Chưa có báo cáo nào được xuất bản."));

  const err = el("div", { class: "alert err hidden" });
  const save = el("button", { class: "btn primary" }, "💾 Lưu quyền");
  wrap.append(err, el("div", { class: "toolbar" }, save));
  const close = showModal("🔑 Phân quyền báo cáo — " + (u.fullName || u.username), wrap, { wide: true });

  save.addEventListener("click", async () => {
    err.classList.add("hidden");
    const templateIds = allBoxes.filter(c => c.checked).map(c => parseInt(c.value, 10));
    save.disabled = true; save.innerHTML = '<span class="spinner"></span> Đang lưu…';
    try {
      await api(`/api/admin/users/${u.userId}/permissions`, { method: "PUT", body: { templateIds } });
      close();
      toast(`Đã cập nhật quyền cho ${u.username} (${templateIds.length} báo cáo).`);
    } catch (ex) { showErr(err, ex.message || "Lưu thất bại."); }
    finally { save.disabled = false; save.innerHTML = "💾 Lưu quyền"; }
  });
}

async function openCreateUser(body) {
  let lookups;
  try { lookups = await fetchLookups(); } catch (ex) { return toast(ex.message, "err"); }
  const uName = el("input", { autocomplete: "off", placeholder: "vd: nguyenvana" });
  const uFull = el("input", { placeholder: "vd: Nguyễn Văn A" });
  const uEmail = el("input", { type: "email", placeholder: "vd: nguyenvana@company.com" });
  const uDept = el("input", { placeholder: "vd: Phòng Kinh doanh" });
  const uPwd = el("input", { value: "Temp@123456" });
  const rolesWrap = el("div", { class: "roles-grid" });
  // Vai trò nghiệp vụ hay dùng khi tạo nhân viên.
  for (const r of lookups.roles) {
    const cb = el("input", { type: "checkbox", value: r.name, "data-urole": "1" });
    rolesWrap.append(el("label", {}, cb, r.name));
  }
  const err = el("div", { class: "alert err hidden" });
  const body2 = el("div", {},
    field("Tên đăng nhập *", uName), field("Họ và tên", uFull), field("Email", uEmail),
    field("Phòng ban", uDept), field("Mật khẩu khởi tạo (tạm) *", uPwd),
    el("div", { class: "field" }, el("label", {}, "Vai trò"), rolesWrap),
    el("p", { class: "muted", style: "font-size:12px" }, "📧 Nếu có email, hệ thống sẽ tự gửi email chào mừng kèm mật khẩu tạm cho nhân viên."),
    err);
  const save = el("button", { class: "btn primary" }, "💾 Tạo tài khoản");
  body2.append(el("div", { class: "toolbar" }, save));
  const close = showModal("Thêm mới tài khoản", body2, { wide: true });

  save.addEventListener("click", async () => {
    err.classList.add("hidden");
    if (!uName.value.trim() || !uPwd.value) return showErr(err, "Tên đăng nhập và mật khẩu là bắt buộc.");
    const roles = [...rolesWrap.querySelectorAll('[data-urole="1"]:checked')].map(c => c.value);
    save.disabled = true; save.innerHTML = '<span class="spinner"></span> Đang tạo…';
    try {
      const res = await api("/api/admin/users", { method: "POST", body: {
        username: uName.value.trim(), fullName: uFull.value.trim() || null, email: uEmail.value.trim() || null,
        department: uDept.value.trim() || null, password: uPwd.value, roles
      }});
      close();
      adminUsers(body);
      if (res.emailSent) toast(`Đã tạo tài khoản & gửi email chào mừng (chế độ ${res.emailMode}). Mở "📧 Hộp thư (demo)" để xem.`);
      else if (res.emailMode === "None") toast("Đã tạo tài khoản (không có email để gửi).");
      else toast(`Đã tạo tài khoản nhưng gửi email thất bại: ${res.emailDetail || res.emailMode}`, "warn");
    } catch (ex) { showErr(err, ex.message || "Tạo tài khoản thất bại."); }
    finally { save.disabled = false; save.innerHTML = "💾 Tạo tài khoản"; }
  });
}

/* ---- Nhập người dùng hàng loạt từ CSV ---- */
const BULK_HEADERS = ["Username", "FullName", "Email", "Department", "Roles", "Password"];

// Parser CSV nhỏ gọn: hỗ trợ ô có dấu phẩy/đóng mở ngoặc kép, xuống dòng CRLF/LF.
function parseCsv(text) {
  const rows = []; let row = [], cur = "", q = false;
  text = text.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (q) {
      if (ch === '"') { if (text[i + 1] === '"') { cur += '"'; i++; } else q = false; }
      else cur += ch;
    } else {
      if (ch === '"') q = true;
      else if (ch === ",") { row.push(cur); cur = ""; }
      else if (ch === "\n") { row.push(cur); rows.push(row); row = []; cur = ""; }
      else cur += ch;
    }
  }
  if (cur !== "" || row.length) { row.push(cur); rows.push(row); }
  return rows.filter(r => r.some(c => c.trim() !== ""));
}

// Chuyển các dòng CSV -> mảng object theo header (bỏ dấu để khớp linh hoạt).
function csvToUsers(text) {
  const rows = parseCsv(text);
  if (rows.length < 2) return { users: [], error: "File cần có dòng tiêu đề và ít nhất 1 dòng dữ liệu." };
  const norm = (s) => s.trim().toLowerCase().replace(/\s+/g, "");
  const header = rows[0].map(norm);
  const idx = {};
  BULK_HEADERS.forEach(h => { idx[h] = header.indexOf(norm(h)); });
  if (idx.Username < 0) return { users: [], error: 'Thiếu cột "Username" trong tiêu đề.' };
  const users = rows.slice(1).map(r => ({
    username: (r[idx.Username] || "").trim(),
    fullName: idx.FullName >= 0 ? (r[idx.FullName] || "").trim() : "",
    email: idx.Email >= 0 ? (r[idx.Email] || "").trim() : "",
    department: idx.Department >= 0 ? (r[idx.Department] || "").trim() : "",
    roles: idx.Roles >= 0 ? (r[idx.Roles] || "").trim() : "",
    password: idx.Password >= 0 ? (r[idx.Password] || "").trim() : ""
  }));
  return { users, error: null };
}

function downloadCsvTemplate() {
  const sample = [
    BULK_HEADERS.join(","),
    'nguyenvanan,Nguyễn Văn An,nguyenvanan@congty.vn,Phòng Kinh doanh,Sales_User,',
    'tranthibinh,Trần Thị Bình,tranthibinh@congty.vn,Phòng Kho bãi,Warehouse_User,',
    'lehoangcuong,Lê Hoàng Cường,lehoangcuong@congty.vn,Phòng Mua hàng,Purchasing_User,'
  ].join("\n");
  const blob = new Blob(["﻿" + sample], { type: "text/csv;charset=utf-8" });
  const a = el("a", { href: URL.createObjectURL(blob), download: "mau-nhap-nguoi-dung.csv" });
  document.body.append(a); a.click(); a.remove();
}

async function openBulkImport(body) {
  const fileInput = el("input", { type: "file", accept: ".csv,text/csv" });
  const textArea = el("textarea", { class: "sql", placeholder: "…hoặc dán nội dung CSV vào đây", style: "height:120px" });
  const sendEmail = el("input", { type: "checkbox" });
  const preview = el("div", {});
  const err = el("div", { class: "alert err hidden" });
  const importBtn = el("button", { class: "btn primary", disabled: "" }, "⬆ Nhập danh sách");
  let parsed = [];

  const refreshPreview = (text) => {
    err.classList.add("hidden"); preview.innerHTML = "";
    const { users, error } = csvToUsers(text);
    if (error) { parsed = []; importBtn.disabled = true; return showErr(err, error); }
    parsed = users;
    const bad = users.filter(u => !u.username).length;
    const table = el("table");
    table.append(el("thead", {}, rowH(["#", "Username", "Họ tên", "Email", "Phòng ban", "Vai trò"])));
    const tb = el("tbody");
    users.slice(0, 50).forEach((u, i) => tb.append(el("tr", { ...(u.username ? {} : { class: "sel" }) },
      td(i + 1), td(u.username || "⚠ thiếu"), td(u.fullName), td(u.email), td(u.department), td(u.roles))));
    table.append(tb);
    preview.append(
      el("p", { class: "info", style: "margin:10px 0 6px" }, `Đọc được ${users.length} dòng` + (bad ? ` · ⚠ ${bad} dòng thiếu Username` : "") + (users.length > 50 ? " · xem trước 50 dòng đầu" : "")),
      el("div", { class: "table-wrap" }, table));
    importBtn.disabled = users.length === 0;
  };

  fileInput.onchange = () => {
    const f = fileInput.files[0]; if (!f) return;
    const rd = new FileReader();
    rd.onload = () => { textArea.value = rd.result; refreshPreview(rd.result); };
    rd.readAsText(f, "utf-8");
  };
  textArea.oninput = () => refreshPreview(textArea.value);

  const wrap = el("div", {},
    el("p", { class: "muted", style: "font-size:13px" },
      "Cột: ", el("code", { class: "inline" }, BULK_HEADERS.join(", ")),
      ". Nhiều vai trò cách nhau bằng ", el("code", { class: "inline" }, ";"),
      ". Để trống Password → mật khẩu tạm ", el("code", { class: "inline" }, "Temp@123456"),
      " (buộc đổi lần đầu)."),
    el("div", { class: "toolbar" },
      el("button", { class: "btn ghost sm", type: "button", onclick: downloadCsvTemplate }, "⬇ Tải CSV mẫu"),
      fileInput),
    field("Nội dung CSV", textArea),
    el("label", { class: "check" }, sendEmail, "Gửi email chào mừng cho người có email"),
    err, preview,
    el("div", { class: "toolbar" }, importBtn));
  const close = showModal("⬆ Nhập người dùng hàng loạt (CSV)", wrap, { wide: true });

  importBtn.addEventListener("click", async () => {
    if (!parsed.length) return;
    importBtn.disabled = true; importBtn.innerHTML = '<span class="spinner"></span> Đang nhập…';
    try {
      const res = await api("/api/admin/users/bulk", { method: "POST", body: { users: parsed, sendEmail: sendEmail.checked } });
      close();
      showBulkResult(res);
      adminUsers(body);
    } catch (ex) { showErr(err, ex.message || "Nhập thất bại."); importBtn.disabled = false; importBtn.innerHTML = "⬆ Nhập danh sách"; }
  });
}

function showBulkResult(res) {
  const table = el("table");
  table.append(el("thead", {}, rowH(["Dòng", "Username", "Kết quả"])));
  const tb = el("tbody");
  res.results.forEach(r => tb.append(el("tr", { ...(r.created ? {} : { class: "sel" }) },
    td(r.row), td(r.username),
    el("td", {}, el("span", { class: "badge " + (r.created ? "ok" : (r.status.startsWith("bỏ qua") ? "warn" : "err")) }, r.status)))));
  table.append(tb);
  const wrap = el("div", {},
    el("p", { class: "info" }, `✅ Tạo mới: ${res.created} · ⚠ Bỏ qua/Lỗi: ${res.failed}`),
    el("div", { class: "table-wrap" }, table));
  showModal("Kết quả nhập hàng loạt", wrap, { wide: true });
}

async function openOutbox() {
  let mails;
  try { mails = await api("/api/admin/outbox"); } catch (ex) { return toast(ex.message, "err"); }
  const list = el("div", { class: "mail-list" });
  const preview = el("iframe", { class: "mail-preview", sandbox: "" });
  if (mails.length === 0) list.append(el("div", { class: "item muted" }, "Chưa có email nào được gửi."));
  mails.forEach((m, i) => {
    const item = el("div", { class: "item" + (i === 0 ? " active" : "") },
      el("div", {}, el("b", {}, m.subject)),
      el("div", { class: "muted", style: "font-size:12px" },
        `→ ${m.to} · ${new Date(m.sentAtUtc).toLocaleString("vi-VN")} · ${m.mode} · ${m.sent ? "✓ đã gửi" : "✗ lỗi"}`));
    item.addEventListener("click", () => {
      list.querySelectorAll(".item").forEach(x => x.classList.remove("active"));
      item.classList.add("active");
      preview.srcdoc = m.bodyHtml;
    });
    list.append(item);
  });
  const body = el("div", {}, list, preview);
  showModal("📧 Hộp thư demo (" + mails.length + " email)", body, { wide: true });
  if (mails.length) preview.srcdoc = mails[0].bodyHtml;
}

async function setActive(id, active) {
  try { await api(`/api/admin/users/${id}/active`, { method: "POST", body: { isActive: active } }); adminUsers($("#adminBody")); }
  catch (ex) { toast(ex.message, "err"); }
}
async function resetPwd(id, username) {
  const pwd = prompt("Nhập mật khẩu mới cho " + username + " (tối thiểu 6 ký tự):");
  if (!pwd) return;
  try { await api(`/api/admin/users/${id}/reset-password`, { method: "POST", body: { newPassword: pwd } }); toast("Đã đổi mật khẩu."); }
  catch (ex) { toast(ex.message, "err"); }
}

async function adminConnections(body) {
  body.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
  try {
    const conns = await api("/api/admin/connections");
    const table = el("table");
    table.append(el("thead", {}, rowH(["Tên", "Máy chủ", "Database", "Tài khoản", "Môi trường", "Mật khẩu", "Trạng thái", "Kiểm tra"])));
    const tb = el("tbody");
    for (const cn of conns) {
      const test = el("button", { class: "btn ghost sm", onclick: (e) => testConn(cn.connectionId, e.target) }, "Test");
      tb.append(el("tr", {},
        td(cn.connectionName), td(cn.serverHost + (cn.port ? ":" + cn.port : "")), td(cn.databaseName),
        td(cn.username), td(cn.environment),
        el("td", {}, el("span", { class: "badge " + (cn.passwordEncrypted ? "ok" : "warn") }, cn.passwordEncrypted ? "Đã mã hóa" : "Chưa mã hóa")),
        el("td", {}, el("span", { class: "badge " + (cn.isActive ? "ok" : "mut") }, cn.isActive ? "Bật" : "Tắt")),
        el("td", {}, test)));
    }
    table.append(tb);
    body.innerHTML = "";
    body.append(el("div", { class: "table-wrap" }, table));
  } catch (ex) { body.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; }
}

async function testConn(id, btn) {
  const old = btn.textContent; btn.disabled = true; btn.textContent = "…";
  try { const r = await api(`/api/admin/connections/${id}/test`, { method: "POST" }); toast(r.message + ` (${r.durationMs} ms)`, r.ok ? "ok" : "err"); }
  catch (ex) { toast(ex.message, "err"); }
  finally { btn.disabled = false; btn.textContent = old; }
}

async function adminTemplates(body) {
  body.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
  try {
    const tpls = await api("/api/admin/templates");
    const table = el("table");
    table.append(el("thead", {}, rowH(["Mã", "Tên báo cáo", "Nhóm", "Kết nối", "Tham số", "Trạng thái", "Thao tác"])));
    const tb = el("tbody");
    const map = { Published: "ok", Draft: "warn", Disabled: "mut" };
    for (const t of tpls) {
      const edit = el("button", { class: "btn ghost sm", onclick: () => adminTemplateEditor(body, t.templateId) }, "✏ Sửa");
      const del = el("button", { class: "btn ghost sm", onclick: () => deleteTemplate(body, t.templateId, t.templateName) }, "🗑 Xóa");
      tb.append(el("tr", {}, td(t.templateCode), td(t.templateName), td(t.categoryName), td(t.connectionName),
        tdNum(t.parameterCount), el("td", {}, el("span", { class: "badge " + (map[t.status] || "mut") }, t.status)),
        el("td", {}, edit, " ", del)));
    }
    table.append(tb);
    body.innerHTML = "";
    const bar = el("div", { class: "toolbar" },
      el("button", { class: "btn primary", onclick: () => adminTemplateEditor(body, null) }, "➕ Tạo báo cáo mới"),
      el("span", { class: "info" }, `${tpls.length} báo cáo`));
    body.append(bar, el("div", { class: "table-wrap" }, table));
  } catch (ex) { body.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; }
}

async function deleteTemplate(body, id, name) {
  if (!confirm(`Xóa báo cáo "${name}"? Thao tác này ẩn báo cáo khỏi hệ thống (xóa mềm).`)) return;
  try { await api("/api/admin/templates/" + id, { method: "DELETE" }); toast("Đã xóa báo cáo."); adminTemplates(body); }
  catch (ex) { toast(ex.message, "err"); }
}

const DATA_TYPES = ["Text", "Integer", "Decimal", "Date", "DateTime", "Dropdown", "Checkbox"];

async function fetchLookups() {
  if (!State.lookups) State.lookups = await api("/api/admin/lookups");
  return State.lookups;
}

async function adminTemplateEditor(body, id) {
  body.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
  let lookups, tpl = null;
  try {
    lookups = await fetchLookups();
    if (id) tpl = await api("/api/admin/templates/" + id);
  } catch (ex) { body.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; return; }

  const g = (v, d) => (v == null ? d : v);
  const form = el("form", { class: "panel tpl-form" });
  form.append(el("h3", {}, id ? "✏ Sửa báo cáo: " + tpl.templateName : "➕ Tạo báo cáo mới"));

  // ---- thông tin chung ----
  const fCode = inputField("Mã báo cáo *", "code", g(tpl?.templateCode, ""), { placeholder: "VD: RPT_MY_REPORT" });
  const fName = inputField("Tên báo cáo *", "name", g(tpl?.templateName, ""));
  const fStatus = selectField("Trạng thái", "status", ["Draft", "Published", "Disabled"], g(tpl?.status, "Draft"));
  const fCat = selectFieldLookup("Nhóm báo cáo *", "category", lookups.categories, tpl?.categoryId);
  const fConn = selectFieldLookup("Kết nối DB *", "connection", lookups.connections, tpl?.connectionId);
  const fMax = inputField("Số dòng tối đa", "maxrows", g(tpl?.maxRows, 10000), { type: "number", min: 1 });
  const fTimeout = inputField("Timeout (giây)", "timeout", g(tpl?.timeoutSeconds, 30), { type: "number", min: 1 });
  form.append(el("div", { class: "row" }, fCode.wrap, fName.wrap, fStatus.wrap));
  form.append(el("div", { class: "row" }, fCat.wrap, fConn.wrap, fMax.wrap, fTimeout.wrap,
    checkField("allowcopy", "Cho phép sao chép", g(tpl?.allowCopy, true)),
    checkField("allowexport", "Cho phép xuất Excel", g(tpl?.allowExport, true))));

  const fDesc = inputField("Mô tả", "desc", g(tpl?.description, ""));
  fDesc.wrap.classList.add("full");
  form.append(fDesc.wrap);

  // ---- SQL ----
  const sqlWrap = el("div", { class: "field full" });
  sqlWrap.append(el("label", {}, "Câu lệnh SQL (chỉ SELECT; tham số dạng ", el("code", { class: "inline" }, "@TenThamSo"), ") *"));
  const sqlArea = el("textarea", { class: "sql", name: "sql",
    placeholder: "SELECT ... FROM ... WHERE Cot >= @TuNgay AND Cot < @DenNgay" }, g(tpl?.sqlCommand, ""));
  sqlWrap.append(sqlArea);
  const sqlMsg = el("div", { class: "alert hidden", id: "sqlMsg" });
  const sqlBtn = el("button", { class: "btn ghost sm", type: "button",
    onclick: () => validateSql(sqlArea.value, sqlMsg) }, "🛡 Kiểm tra SQL (SQL Guard)");
  sqlWrap.append(el("div", { class: "toolbar" }, sqlBtn), sqlMsg);
  form.append(sqlWrap);

  // ---- tham số ----
  form.append(el("h3", { style: "margin-top:18px" }, "Tham số đầu vào"));
  const ptable = el("table", { class: "param-table" });
  ptable.append(el("thead", {}, rowH(["Tên (@)", "Nhãn hiển thị", "Kiểu", "Bắt buộc", "Mặc định", "Min", "Max", "MaxLen", "Options (JSON)", ""])));
  const ptbody = el("tbody");
  ptable.append(ptbody);
  const addParamRow = (p) => ptbody.append(buildParamRow(p));
  (tpl?.parameters || []).forEach(addParamRow);
  form.append(el("div", { class: "table-wrap" }, ptable));
  form.append(el("div", { class: "toolbar" },
    el("button", { class: "btn ghost sm", type: "button", onclick: () => addParamRow(null) }, "➕ Thêm tham số")));

  // ---- phân quyền theo vai trò ----
  form.append(el("h3", { style: "margin-top:18px" }, "Cấp quyền truy cập theo vai trò"));
  const rolesGrid = el("div", { class: "roles-grid" });
  const grantedRoles = new Set(tpl?.roleIds || []);
  for (const r of lookups.roles) {
    const cb = el("input", { type: "checkbox", value: r.id, "data-role": "1", ...(grantedRoles.has(r.id) ? { checked: "" } : {}) });
    rolesGrid.append(el("label", {}, cb, r.name));
  }
  form.append(rolesGrid);

  // ---- actions ----
  const errBox = el("div", { class: "alert err hidden", id: "tplErr" });
  const saveBtn = el("button", { class: "btn primary", type: "submit" }, id ? "💾 Lưu thay đổi" : "💾 Tạo báo cáo");
  const cancelBtn = el("button", { class: "btn ghost", type: "button", onclick: () => adminTemplates(body) }, "Hủy");
  form.append(errBox, el("div", { class: "sticky-actions" }, saveBtn, cancelBtn));

  form.addEventListener("submit", (e) => { e.preventDefault(); saveTemplate(body, id, form, ptbody, saveBtn, errBox); });
  body.innerHTML = "";
  body.append(form);
}

function buildParamRow(p) {
  const nameI = el("input", { "data-f": "name", value: p?.parameterName || "", placeholder: "@TuNgay" });
  const dispI = el("input", { "data-f": "display", value: p?.displayName || "" });
  const typeS = el("select", { "data-f": "type" });
  DATA_TYPES.forEach(dt => typeS.append(el("option", { value: dt, ...(p?.dataType === dt ? { selected: "" } : {}) }, dt)));
  const reqI = el("input", { type: "checkbox", "data-f": "req", ...(p?.isRequired ? { checked: "" } : {}) });
  const defI = el("input", { "data-f": "def", value: p?.defaultValue ?? "" });
  const minI = el("input", { class: "narrow", "data-f": "min", value: p?.minValue ?? "" });
  const maxI = el("input", { class: "narrow", "data-f": "max", value: p?.maxValue ?? "" });
  const lenI = el("input", { class: "narrow", type: "number", "data-f": "len", value: p?.maxLength ?? "" });
  const optI = el("input", { "data-f": "opt", value: p?.options ?? "", placeholder: '["A","B"]' });
  const rm = el("button", { class: "btn ghost sm", type: "button" }, "✕");
  const tr = el("tr", {},
    el("td", {}, nameI), el("td", {}, dispI), el("td", {}, typeS),
    el("td", { style: "text-align:center" }, reqI), el("td", {}, defI),
    el("td", {}, minI), el("td", {}, maxI), el("td", {}, lenI), el("td", {}, optI), el("td", {}, rm));
  rm.addEventListener("click", () => tr.remove());
  return tr;
}

function collectParamRows(ptbody) {
  const params = [];
  ptbody.querySelectorAll("tr").forEach(tr => {
    const v = (f) => { const e = tr.querySelector(`[data-f="${f}"]`); return e ? e.value.trim() : ""; };
    const name = v("name");
    if (!name) return;
    const lenRaw = v("len");
    params.push({
      parameterName: name,
      displayName: v("display") || name,
      dataType: v("type"),
      isRequired: tr.querySelector('[data-f="req"]').checked,
      defaultValue: v("def") || null,
      displayOrder: 0,
      minValue: v("min") || null,
      maxValue: v("max") || null,
      maxLength: lenRaw ? parseInt(lenRaw, 10) : null,
      options: v("opt") || null
    });
  });
  return params;
}

async function validateSql(sql, msgBox) {
  msgBox.classList.add("hidden");
  try {
    const r = await api("/api/admin/templates/validate-sql", { method: "POST", body: { sql } });
    msgBox.className = "alert " + (r.ok ? "ok" : "err");
    msgBox.textContent = r.ok ? "✓ Câu lệnh hợp lệ (chỉ đọc)." : "✗ " + r.reason;
    msgBox.classList.remove("hidden");
  } catch (ex) { toast(ex.message, "err"); }
}

async function saveTemplate(body, id, form, ptbody, btn, errBox) {
  errBox.classList.add("hidden");
  const val = (n) => form.querySelector(`[name="${n}"]`).value;
  const checked = (n) => form.querySelector(`[name="${n}"]`).checked;
  const roleIds = [...form.querySelectorAll('[data-role="1"]:checked')].map(c => parseInt(c.value, 10));
  const req = {
    templateCode: val("code").trim(),
    templateName: val("name").trim(),
    description: val("desc").trim() || null,
    categoryId: parseInt(val("category"), 10) || 0,
    connectionId: parseInt(val("connection"), 10) || 0,
    sqlCommand: val("sql"),
    maxRows: parseInt(val("maxrows"), 10) || 0,
    timeoutSeconds: parseInt(val("timeout"), 10) || 0,
    allowCopy: checked("allowcopy"),
    allowExport: checked("allowexport"),
    status: val("status"),
    roleIds,
    parameters: collectParamRows(ptbody),
    versionNote: null
  };
  btn.disabled = true; btn.innerHTML = '<span class="spinner"></span> Đang lưu…';
  try {
    if (id) await api("/api/admin/templates/" + id, { method: "PUT", body: req });
    else await api("/api/admin/templates", { method: "POST", body: req });
    State.catalog = await api("/api/catalog").catch(() => State.catalog);
    toast(id ? "Đã lưu thay đổi." : "Đã tạo báo cáo mới.");
    adminTemplates(body);
  } catch (ex) {
    errBox.textContent = ex.message || "Lưu thất bại.";
    errBox.classList.remove("hidden");
    errBox.scrollIntoView({ behavior: "smooth", block: "center" });
  } finally { btn.disabled = false; btn.innerHTML = id ? "💾 Lưu thay đổi" : "💾 Tạo báo cáo"; }
}

/* ---- field builders for the editor ---- */
function inputField(label, name, value, attrs = {}) {
  const input = el("input", { name, value: value ?? "", ...attrs });
  const wrap = el("div", { class: "field" }, el("label", {}, label), input);
  return { wrap, input };
}
function selectField(label, name, options, value) {
  const sel = el("select", { name });
  options.forEach(o => sel.append(el("option", { value: o, ...(String(o) === String(value) ? { selected: "" } : {}) }, o)));
  return { wrap: el("div", { class: "field" }, el("label", {}, label), sel), input: sel };
}
function selectFieldLookup(label, name, items, value) {
  const sel = el("select", { name });
  sel.append(el("option", { value: "" }, "-- chọn --"));
  items.forEach(it => sel.append(el("option", { value: it.id, ...(it.id === value ? { selected: "" } : {}) }, it.name)));
  return { wrap: el("div", { class: "field" }, el("label", {}, label), sel), input: sel };
}
function checkField(name, label, value) {
  const cb = el("input", { type: "checkbox", name, ...(value ? { checked: "" } : {}) });
  return el("label", { class: "check" }, cb, label);
}

async function adminAudit(body) {
  body.innerHTML = '<p class="muted"><span class="spinner"></span> Đang tải…</p>';
  try {
    const items = await api("/api/admin/audit");
    const table = el("table");
    table.append(el("thead", {}, rowH(["#", "Người dùng", "Hành động", "Đối tượng", "ID", "IP", "Thời điểm"])));
    const tb = el("tbody");
    for (const a of items)
      tb.append(el("tr", {}, td(a.auditId), td(a.username || "hệ thống"), td(a.action),
        td(a.targetObject || ""), td(a.targetId || ""), td(a.clientIP || ""),
        td(new Date(a.createdAt).toLocaleString("vi-VN"))));
    if (items.length === 0) tb.append(el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Chưa có nhật ký.")));
    table.append(tb);
    body.innerHTML = "";
    body.append(el("div", { class: "table-wrap" }, table));
  } catch (ex) { body.innerHTML = `<div class="alert err">${esc(ex.message)}</div>`; }
}

/* ---------------- helpers ---------------- */
function rowH(cols) { const tr = el("tr"); cols.forEach(c => tr.append(el("th", {}, c))); return tr; }
function td(v) { return el("td", {}, v == null ? "" : String(v)); }
function tdNum(v) { return el("td", { class: "num" }, v == null ? "" : String(v)); }

let toastTimer = null;
function toast(msg, kind = "ok") {
  let t = $("#toast");
  if (!t) { t = el("div", { id: "toast" }); document.body.append(t); }
  t.className = "alert " + kind;
  Object.assign(t.style, { position: "fixed", bottom: "24px", right: "24px", zIndex: 9999, boxShadow: "var(--shadow)", maxWidth: "360px" });
  t.textContent = msg;
  t.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add("hidden"), 4000);
}

/* ---------------- boot ---------------- */
(async function boot() {
  if (State.token) {
    try { State.user = await api("/api/auth/me"); await startApp(); }
    catch { doLogout(false); }
  }
})();
