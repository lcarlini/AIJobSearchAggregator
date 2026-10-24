const statusLabel = {
  new: "Nova",
  seen: "Vista",
  saved: "Salva",
  applied: "Candidatado",
  interview: "Entrevista",
  offer: "Oferta",
  rejected: "Recusado",
  hidden: "Oculta"
};

const workplaceLabel = {
  remote: "Remoto",
  hybrid: "Híbrido",
  onsite: "Presencial",
  unknown: "Não informa"
};

const state = {
  jobs: [],
  jobsKey: "",
  selectedId: null,
  detail: null,
  detailLoading: false,
  profiles: [],
  meta: null,
  editingId: null,
  running: false,
  logCount: 0,
  filters: loadFilters()
};

let selectToken = 0;
let pollTimer = 0;

function loadFilters() {
  const fallback = { q: "", status: "all", region: "all", workplace: "all", onlyRelevant: true, onlySalary: false };
  try {
    return { ...fallback, ...JSON.parse(sessionStorage.getItem("vagas.filters") || "{}") };
  } catch {
    return fallback;
  }
}

function saveFilters() {
  sessionStorage.setItem("vagas.filters", JSON.stringify(state.filters));
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

function setProblem(text) {
  document.querySelector("#problem").textContent = text || "";
}

function regionName(key) {
  return state.meta?.regions?.find(region => region.key === key)?.name || key;
}

function formatWhen(value) {
  if (!value) return "";
  const date = value.length <= 10 ? new Date(`${value}T12:00:00`) : new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString("pt-BR", { day: "2-digit", month: "short" });
}

async function readJson(response) {
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || "A operação falhou.");
  return data;
}

function queryString() {
  const params = new URLSearchParams();
  if (state.filters.q) params.set("q", state.filters.q);
  if (state.filters.status !== "all") params.set("status", state.filters.status);
  if (state.filters.region !== "all") params.set("region", state.filters.region);
  if (state.filters.workplace !== "all") params.set("workplace", state.filters.workplace);
  params.set("onlyRelevant", String(state.filters.onlyRelevant));
  params.set("onlySalary", String(state.filters.onlySalary));
  if (state.filters.status === "hidden") params.set("includeHidden", "true");
  return params.toString();
}

async function loadJobs() {
  const response = await fetch(`/api/jobs?${queryString()}`);
  state.jobs = await readJson(response);
  renderList();
}

async function loadProfiles() {
  state.profiles = await readJson(await fetch("/api/profiles"));
  renderProfiles();
}

async function loadMeta() {
  state.meta = await readJson(await fetch("/api/meta"));
  const help = document.querySelector("#cityHelp");
  help.replaceChildren();
  for (const region of state.meta.regions) {
    const title = el("p", null, `${region.name}. ${region.summary}`);
    help.append(title);
    if (region.cities?.length) help.append(el("p", null, region.cities.join(", ") + "."));
  }
}

function renderStats(stats) {
  const box = document.querySelector("#stats");
  box.replaceChildren();
  const items = [
    [stats?.newCount ?? 0, "não lidas"],
    [stats?.arrivedToday ?? 0, "chegaram hoje"],
    [stats?.saved ?? 0, "salvas"],
    [stats?.pipeline ?? 0, "candidaturas"]
  ];
  for (const [value, label] of items) {
    const block = el("div");
    block.append(el("strong", null, String(value)), el("span", null, label));
    box.append(block);
  }
}

function renderLog(lines) {
  const log = document.querySelector("#log");
  if (lines.length < state.logCount) {
    log.replaceChildren();
    state.logCount = 0;
  }
  for (let index = state.logCount; index < lines.length; index++) {
    const line = el("div", lines[index].level, `[${lines[index].time}] ${lines[index].message}`);
    log.append(line);
  }
  if (lines.length !== state.logCount) {
    state.logCount = lines.length;
    log.scrollTop = log.scrollHeight;
  }
}

function renderList() {
  const list = document.querySelector("#list");
  const key = state.jobs.map(job => [job.id, job.status, job.salary, job.summary, (job.stack || []).join(), job.workplace, job.relevant].join("~")).join("|");
  document.querySelector("#listCount").textContent = state.jobs.length === 1 ? "1 vaga" : `${state.jobs.length} vagas`;
  if (key === state.jobsKey && list.childElementCount) {
    for (const card of list.querySelectorAll(".card"))
      card.classList.toggle("selected", Number(card.dataset.id) === state.selectedId);
    return;
  }

  state.jobsKey = key;
  const scroll = list.scrollTop;
  list.replaceChildren();
  if (!state.jobs.length) {
    const empty = el("div", "empty");
    empty.append(
      el("h2", null, "Nada neste filtro"),
      el("p", null, "A busca roda sozinha ao abrir. Se o LinkedIn ainda estiver respondendo, as vagas entram aqui. Ocultar uma vaga tira ela desta lista.")
    );
    list.append(empty);
    return;
  }

  for (const job of state.jobs) {
    const card = el("button", "card");
    card.type = "button";
    card.dataset.id = String(job.id);
    if (job.id === state.selectedId) card.classList.add("selected");

    if (job.logoUrl) {
      const img = el("img", "logo");
      img.src = job.logoUrl;
      img.alt = "";
      img.addEventListener("error", () => img.replaceWith(monogram(job.company)));
      card.append(img);
    } else {
      card.append(monogram(job.company));
    }

    const body = el("div");
    const top = el("div", "card-top");
    top.append(el("h2", null, job.title));
    top.append(el("span", `status status-${job.status}`, statusLabel[job.status] || job.status));
    body.append(top);
    const place = [job.company, job.location, workplaceLabel[job.workplace] || ""].filter(Boolean).join(" · ");
    body.append(el("div", "meta", place));
    const subParts = [job.listedAgo, job.seniority, job.employment, formatWhen(job.postedAt)].filter(Boolean);
    if (subParts.length) body.append(el("div", "sub", subParts.join(" · ")));
    const tags = el("div", "tags");
    if (job.salary) tags.append(el("span", "salary", job.salary));
    for (const region of job.regions || []) tags.append(el("span", "tag", regionName(region)));
    for (const skill of (job.stack || []).slice(0, 6)) tags.append(el("span", "tag", skill));
    if (job.insight) tags.append(el("span", "tag", job.insight));
    if (tags.childElementCount) body.append(tags);
    if (job.summary) body.append(el("p", "sub", job.summary));
    card.append(body);
    card.addEventListener("click", () => selectJob(job.id));
    list.append(card);
  }
  list.scrollTop = scroll;
}

function monogram(company) {
  return el("span", "monogram", (company || "?").trim().charAt(0).toUpperCase());
}

function renderDetail() {
  const pane = document.querySelector("#detail");
  const note = document.querySelector("#note");
  if (note && document.activeElement === note) return;
  pane.classList.toggle("open", Boolean(state.detail));
  if (!state.detail) {
    pane.replaceChildren();
    const empty = el("div", "empty-detail");
    empty.append(
      el("h2", null, "Escolha uma vaga"),
      el("p", null, "Local, salário, stack e o link para se candidatar aparecem aqui. Ocultar tira a vaga da mesa e ela não volta como nova.")
    );
    pane.append(empty);
    return;
  }

  const job = state.detail;
  pane.replaceChildren();
  if (window.matchMedia("(max-width: 1100px)").matches) {
    const close = el("button", null, "Fechar");
    close.type = "button";
    close.addEventListener("click", () => {
      state.selectedId = null;
      state.detail = null;
      pane.classList.remove("open");
      renderDetail();
      renderList();
    });
    pane.append(close);
  }

  pane.append(el("p", "kicker", (job.regions || []).map(regionName).join(" · ") || "Região não marcada"));
  pane.append(el("h2", null, job.title));
  const company = el("p", "company", job.company || "Empresa não informada");
  if (job.companyUrl) {
    const link = el("a", null, job.company);
    link.href = job.companyUrl;
    link.target = "_blank";
    link.rel = "noreferrer";
    company.replaceChildren(link);
  }
  pane.append(company);

  const facts = el("div", "facts");
  fact(facts, "Onde", job.location || "Não informado");
  fact(facts, "Modalidade", workplaceLabel[job.workplace] || "Não informa");
  fact(facts, "Salário", job.salary || "Não publicado");
  fact(facts, "Senioridade", job.seniority || "Não informada");
  fact(facts, "Contrato", job.employment || "Não informado");
  fact(facts, "Publicada", [job.listedAgo, formatWhen(job.postedAt)].filter(Boolean).join(" · ") || "Sem data");
  fact(facts, "Área", job.jobFunction || "Não informada");
  fact(facts, "Setor", job.industry || "Não informado");
  pane.append(facts);

  if ((job.stack || []).length) {
    const tags = el("div", "tags");
    for (const skill of job.stack) tags.append(el("span", "tag", skill));
    pane.append(tags);
  }

  const links = el("div", "links");
  if (job.applyUrl) links.append(linkButton(job.applyUrl.includes("linkedin.com") ? "Candidatar no LinkedIn" : "Candidatar no site", job.applyUrl, true));
  if (job.sourceUrl && job.sourceUrl !== job.applyUrl) links.append(linkButton("Ver no LinkedIn", job.sourceUrl, false));
  const copy = el("button", null, "Copiar link");
  copy.type = "button";
  copy.addEventListener("click", async () => {
    await navigator.clipboard.writeText(job.applyUrl || job.sourceUrl || "");
    copy.textContent = "Link copiado";
  });
  links.append(copy);
  pane.append(links);

  const actions = el("div", "actions");
  actions.append(statusButton("Salvar", "saved"));
  actions.append(statusButton("Candidatado", "applied"));
  actions.append(statusButton("Entrevista", "interview"));
  actions.append(statusButton("Oferta", "offer"));
  actions.append(statusButton("Recusado", "rejected"));
  actions.append(statusButton(job.status === "hidden" ? "Trazer de volta" : "Ocultar", job.status === "hidden" ? "seen" : "hidden"));
  pane.append(actions);

  if (job.appliedAt) pane.append(el("p", "muted", `Candidatura em ${formatWhen(job.appliedAt)}`));
  if (job.insight) pane.append(el("p", "muted", job.insight));

  const noteBox = el("div", "note-box");
  noteBox.append(el("span", "kicker", "Nota"));
  const area = el("textarea");
  area.id = "note";
  area.placeholder = "Onde parei, com quem falei, pretensão enviada";
  area.value = job.note || "";
  noteBox.append(area);
  const saveNote = el("button", null, "Salvar nota");
  saveNote.type = "button";
  saveNote.addEventListener("click", () => patchJob(job.status));
  noteBox.append(saveNote);
  pane.append(noteBox);

  pane.append(el("h3", null, "Descrição"));
  if (state.detailLoading) pane.append(el("p", null, "Lendo a vaga no LinkedIn…"));
  else pane.append(el("div", "description", job.description || job.summary || "A descrição pública ainda não entrou. O link de candidatura acima já funciona."));
  pane.append(el("p", "muted", "Fonte: LinkedIn"));
}

function fact(parent, label, value) {
  const box = el("div");
  box.append(el("span", null, label), document.createTextNode(value));
  parent.append(box);
}

function linkButton(label, href, primary) {
  const link = el("a", primary ? "primary" : "", label);
  link.href = href;
  link.target = "_blank";
  link.rel = "noreferrer";
  styleLink(link, primary);
  return link;
}

function styleLink(link, primary) {
  link.style.display = "inline-block";
  link.style.textDecoration = "none";
  link.style.borderRadius = "999px";
  link.style.padding = "0.4rem 0.8rem";
  link.style.border = "1px solid var(--line)";
  if (primary) {
    link.style.background = "var(--green)";
    link.style.color = "var(--green-ink)";
    link.style.borderColor = "var(--green)";
  }
}

function statusButton(label, status) {
  const button = el("button", state.detail?.status === status ? "primary" : "", label);
  button.type = "button";
  button.addEventListener("click", () => patchJob(status));
  return button;
}

async function selectJob(id) {
  const token = ++selectToken;
  state.selectedId = id;
  state.detailLoading = false;
  renderList();
  try {
    const job = await readJson(await fetch(`/api/jobs/${id}?markSeen=true`));
    if (token !== selectToken) return;
    state.detail = job;
    renderDetail();
    if (!job.hasDescription) {
      state.detailLoading = true;
      renderDetail();
      const response = await fetch(`/api/jobs/${id}/detail`, { method: "POST" });
      if (token !== selectToken) return;
      state.detailLoading = false;
      if (response.ok) state.detail = await response.json();
      else setProblem("Não consegui ler a descrição agora. O link da vaga continua disponível.");
      renderDetail();
    }
    await loadJobs();
    await refreshStatus();
  } catch (error) {
    if (token === selectToken) setProblem(error.message);
  }
}

async function patchJob(status) {
  if (!state.detail) return;
  const note = document.querySelector("#note")?.value ?? state.detail.note ?? "";
  try {
    state.detail = await readJson(await fetch(`/api/jobs/${state.detail.id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ status, note })
    }));
    if (status === "hidden" && state.filters.status !== "hidden") {
      state.selectedId = null;
      state.detail = null;
    }
    setProblem("");
    renderDetail();
    await loadJobs();
    await refreshStatus();
  } catch (error) {
    setProblem(error.message);
  }
}

function renderProfiles() {
  const list = document.querySelector("#profileList");
  list.replaceChildren();
  for (const profile of state.profiles) {
    const item = el("li", "profile");
    item.append(el("b", null, profile.name));
    const row = el("div", "row");
    const toggle = el("button", null, profile.enabled ? "Ativa" : "Pausada");
    toggle.type = "button";
    toggle.addEventListener("click", () => saveProfile({ ...profile, enabled: !profile.enabled }, profile.id));
    const edit = el("button", null, "Editar");
    edit.type = "button";
    edit.addEventListener("click", () => startEdit(profile));
    const remove = el("button", null, "Excluir");
    remove.type = "button";
    remove.addEventListener("click", () => removeProfile(profile));
    row.append(toggle, edit, remove);
    item.append(row);
    const where = (profile.regions || []).map(regionName).join(", ");
    item.append(el("small", null, `${profile.keywords} · ${where} · ${recencyLabel(profile.recency)} · ${profile.pages} página(s)`));
    list.append(item);
  }
}

function recencyLabel(value) {
  if (value === "day") return "24 horas";
  if (value === "month") return "mês";
  return "semana";
}

function startEdit(profile) {
  const form = document.querySelector("#profileForm");
  form.elements.name.value = profile.name;
  form.elements.keywords.value = profile.keywords;
  form.elements.recency.value = profile.recency;
  form.elements.pages.value = profile.pages;
  form.elements.enabled.checked = profile.enabled;
  for (const box of form.querySelectorAll("input[name=region]"))
    box.checked = profile.regions.includes(box.value);
  state.editingId = profile.id;
  document.querySelector("#profileHeading").textContent = "Editar busca";
  document.querySelector("#profileSave").textContent = "Salvar";
  document.querySelector("#profileCancel").hidden = false;
}

function resetForm() {
  document.querySelector("#profileForm").reset();
  document.querySelector("#profileForm").elements.enabled.checked = true;
  for (const box of document.querySelectorAll("#profileForm input[name=region]")) box.checked = true;
  state.editingId = null;
  document.querySelector("#profileHeading").textContent = "Nova busca";
  document.querySelector("#profileSave").textContent = "Adicionar";
  document.querySelector("#profileCancel").hidden = true;
}

function formProfile() {
  const form = document.querySelector("#profileForm");
  return {
    name: form.elements.name.value.trim(),
    keywords: form.elements.keywords.value.trim(),
    enabled: form.elements.enabled.checked,
    recency: form.elements.recency.value,
    pages: Number(form.elements.pages.value),
    regions: [...form.querySelectorAll("input[name=region]:checked")].map(box => box.value)
  };
}

async function saveProfile(profile, id) {
  const response = await fetch(id ? `/api/profiles/${id}` : "/api/profiles", {
    method: id ? "PUT" : "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(profile)
  });
  await readJson(response);
  await loadProfiles();
}

async function removeProfile(profile) {
  if (!confirm(`Excluir a busca "${profile.name}"? As vagas já salvas continuam na lista.`)) return;
  const response = await fetch(`/api/profiles/${profile.id}`, { method: "DELETE" });
  if (!response.ok && response.status !== 204) await readJson(response);
  if (state.editingId === profile.id) resetForm();
  await loadProfiles();
}

async function refreshStatus() {
  const status = await readJson(await fetch("/api/status"));
  state.running = status.running;
  document.querySelector("#searchNow").disabled = status.running;
  document.querySelector("#searchNow").textContent = status.running ? "Buscando…" : "Buscar agora";
  renderStats(status.stats);
  renderLog(status.log || []);
  const latest = status.runs?.[0];
  document.querySelector("#runLine").textContent = latest?.message || (status.running ? "Buscando no LinkedIn…" : "Pronta para buscar.");
  return status;
}

function schedule(delay) {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(poll, delay);
}

async function poll() {
  try {
    const status = await refreshStatus();
    if (status.running) await loadJobs();
    schedule(status.running ? 1500 : 8000);
  } catch {
    schedule(8000);
  }
}

function applyFilterChips() {
  for (const group of document.querySelectorAll(".chips")) {
    const name = group.dataset.filter;
    for (const button of group.querySelectorAll("button"))
      button.classList.toggle("on", button.dataset.value === state.filters[name]);
  }
  document.querySelector("#q").value = state.filters.q;
  document.querySelector("#onlyRelevant").checked = state.filters.onlyRelevant;
  document.querySelector("#onlySalary").checked = state.filters.onlySalary;
}

function exportCsv() {
  const header = ["titulo", "empresa", "local", "modalidade", "salario", "senioridade", "contrato", "stack", "status", "regioes", "link", "nota"];
  const lines = [header.join(";")];
  for (const job of state.jobs) {
    const cells = [
      job.title, job.company, job.location, workplaceLabel[job.workplace] || "", job.salary, job.seniority,
      job.employment, (job.stack || []).join(", "), statusLabel[job.status] || job.status,
      (job.regions || []).map(regionName).join(", "), job.applyUrl || job.sourceUrl, job.note
    ].map(value => `"${String(value ?? "").replaceAll('"', '""')}"`);
    lines.push(cells.join(";"));
  }
  const blob = new Blob(["\uFEFF" + lines.join("\n")], { type: "text/csv;charset=utf-8" });
  const link = document.createElement("a");
  link.href = URL.createObjectURL(blob);
  link.download = `vagas-${new Date().toISOString().slice(0, 10)}.csv`;
  link.click();
  URL.revokeObjectURL(link.href);
}

document.querySelectorAll(".chips").forEach(group => {
  group.addEventListener("click", async event => {
    const button = event.target.closest("button");
    if (!button) return;
    state.filters[group.dataset.filter] = button.dataset.value;
    saveFilters();
    applyFilterChips();
    state.jobsKey = "";
    await loadJobs();
  });
});

let queryTimer = 0;
document.querySelector("#q").addEventListener("input", event => {
  clearTimeout(queryTimer);
  queryTimer = setTimeout(async () => {
    state.filters.q = event.target.value.trim();
    saveFilters();
    state.jobsKey = "";
    await loadJobs();
  }, 250);
});

for (const id of ["onlyRelevant", "onlySalary"]) {
  document.querySelector("#" + id).addEventListener("change", async event => {
    state.filters[id] = event.target.checked;
    saveFilters();
    state.jobsKey = "";
    await loadJobs();
  });
}

document.querySelector("#searchNow").addEventListener("click", async () => {
  setProblem("");
  document.querySelector("#searchNow").disabled = true;
  await fetch("/api/search", { method: "POST" });
  schedule(200);
});

document.querySelector("#export").addEventListener("click", exportCsv);
document.querySelector("#consoleToggle").addEventListener("click", () => {
  document.body.classList.toggle("console-collapsed");
  document.querySelector("#consoleToggle").textContent = document.body.classList.contains("console-collapsed") ? "Abrir" : "Recolher";
});

document.querySelector("#profileForm").addEventListener("submit", async event => {
  event.preventDefault();
  try {
    await saveProfile(formProfile(), state.editingId);
    resetForm();
    setProblem("");
  } catch (error) {
    setProblem(error.message);
  }
});

document.querySelector("#profileCancel").addEventListener("click", resetForm);

applyFilterChips();
loadMeta().then(loadProfiles).then(loadJobs).then(poll).catch(error => setProblem(error.message));
