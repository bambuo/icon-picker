<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useVirtualizer } from '@tanstack/vue-virtual'
import { Message } from '@arco-design/web-vue'
import { IconFolder } from '@arco-design/web-vue/es/icon'

interface Cand {
  file: string
  score: number
  img: number
  text: number | null
  corr: number | null
  pixel: number | null
}
interface ImageItem {
  imageCode: string
  hasLive: boolean
  pending?: boolean
  top: Cand[]
}
interface Data {
  high: number
  mid: number
  sameImg: number
  poolCount: number
  results: ImageItem[]
}

const data = ref<Data | null>(null)
const picks = ref<Record<string, string>>({})
const filter = ref('all')
const loading = ref(true)
const analyzing = ref(false)
const analyzeProg = ref<{ phase: string; done: number; total: number } | null>(null)
const loadData = async () => { data.value = await (await fetch("/api/data")).json() }

const analyze = async () => {
  if (analyzing.value) return
  analyzing.value = true
  try {
    const resp = await fetch("/api/scoring/compute", { method: "POST" })
    if (!resp.ok) { Message.error((await resp.json()).error ?? "启动失败"); return }
    // 轮询后端两阶段评分（语义排序 → 像素同图比对）
    while (true) {
      const st = await (await fetch("/api/scoring/status")).json()
      analyzeProg.value = { phase: st.phase, done: st.done, total: st.total }
      if (!st.running) break
      await new Promise((r) => setTimeout(r, 1000))
    }
    await loadData()
  } finally {
    analyzing.value = false
    analyzeProg.value = null
  }
}

const theme = ref<'dark' | 'light'>(
  (localStorage.getItem('icon-theme') as 'dark' | 'light') || 'dark',
)

const sendChromeTheme = (t: 'dark' | 'light') => {
  const bridge = (window as any).invokeCSharpAction
  if (typeof bridge !== 'function') return
  const rgba = (name: string) => {
    const el = document.createElement('span')
    el.style.color = `var(${name})`
    document.body.appendChild(el)
    const value = getComputedStyle(el).color
    el.remove()
    const m = value.match(/rgba?\(([^)]+)\)/)
    if (!m) return null
    const parts = m[1].split(',').map((v) => Number(v.trim()))
    return [
      Math.round(parts[0]), Math.round(parts[1]), Math.round(parts[2]),
      parts.length > 3 ? Math.round(parts[3] * 255) : 255,
    ]
  }
  bridge(JSON.stringify({
    type: 'theme',
    value: t,
    bg: rgba('--color-bg-1'),
    line: rgba('--color-border-2'),
    text: rgba('--color-text-2'),
    fill: rgba('--color-fill-2'),
    danger: [245, 63, 63, 255],
  }))
}

watch(theme, (t) => {
  document.body.setAttribute('arco-theme', t)
  document.documentElement.style.colorScheme = t
  localStorage.setItem('icon-theme', t)
  setTimeout(() => sendChromeTheme(t), 0)
}, { immediate: true })

const SAME = computed(() => data.value?.sameImg ?? 0.78)
const HIGH = computed(() => data.value?.high ?? 0.75)
const MID = computed(() => data.value?.mid ?? 0.62)

const tierOf = (g: ImageItem) => {
  if (g.pending) return 'pending'
  const b = g.top[0]
  if (!g.hasLive || !b) return 'none'
  if (b.pixel != null && b.pixel >= SAME.value) return 'same'
  if (b.score >= HIGH.value) return 'high'
  if (b.score >= MID.value) return 'mid'
  return 'low'
}
const tierName: Record<string, string> = {
  same: '同图', high: '高相似', mid: '中相似', low: '低相似', none: '无线上现图', pending: '计算中',
}
const tierColor: Record<string, string> = {
  same: 'magenta', high: 'green', mid: 'orange', low: 'gray', none: 'red', pending: 'gray',
}
const candTier = (c: Cand) => (c.pixel != null && c.pixel >= SAME.value ? 'same' : c.score >= HIGH.value ? 'high' : c.score >= MID.value ? 'mid' : 'low')

const counts = computed(() => {
  const c: Record<string, number> = { all: 0, same: 0, high: 0, mid: 0, low: 0, none: 0, pending: 0, picked: 0 }
  for (const g of data.value?.results ?? []) {
    c.all++
    c[tierOf(g)]++
    if (picks.value[g.imageCode]) c.picked++
  }
  return c
})

const visible = computed(() => (data.value?.results ?? []).filter((g) =>
  filter.value === 'all' ? true
    : filter.value === 'picked' ? !!picks.value[g.imageCode] : tierOf(g) === filter.value
))

// 虚拟滚动：只渲染可视区附近的卡片（WebKit 下超长列表 + 浮层会卡死）
const scrollEl = ref<HTMLElement | null>(null)
const virtualizer = useVirtualizer(computed(() => ({
  count: visible.value.length,
  getScrollElement: () => scrollEl.value,
  estimateSize: () => 430,
  overscan: 3,
  getItemKey: (i: number) => visible.value[i]?.imageCode ?? i,
})))
const vItems = computed(() => virtualizer.value.getVirtualItems())
const vRows = computed(() => vItems.value.flatMap((v) => {
  const g = visible.value[v.index]
  return g ? [{ index: v.index, start: v.start, imageCode: g.imageCode, g }] : []
}))
const vTotal = computed(() => virtualizer.value.getTotalSize())
const measureRow = (el: any) => { if (el) virtualizer.value.measureElement(el as Element) }
watch(filter, () => {
  virtualizer.value.scrollToOffset(0)
  virtualizer.value.measure()
})

const loadPicks = async () => {
  picks.value = await (await fetch('/api/picks')).json()
}
// 启动自检（向量补算 + 评分重算）进行中：显示进度，完成后自动刷新数据
const watchStartupScoring = async () => {
  const get = async (p: string) => (await fetch(p)).json()
  let sawRunning = false
  try {
    while (true) {
      const [e, st] = await Promise.all([get('/api/embed/status'), get('/api/scoring/status')])
      if (e.running || st.running) {
        if (!sawRunning) {
          sawRunning = true
          analyzing.value = true
        }
        analyzeProg.value = e.running
          ? { phase: '补算向量', done: e.done, total: e.total }
          : { phase: st.phase, done: st.done, total: st.total }
        await new Promise((r) => setTimeout(r, 1000))
        continue
      }
      if (!sawRunning) return
      await new Promise((r) => setTimeout(r, 1500))
      const st2 = await get('/api/scoring/status')
      if (!st2.running) break
    }
    await loadData()
  } finally {
    analyzing.value = false
    analyzeProg.value = null
  }
}
onMounted(async () => {
  scrollEl.value = document.querySelector('.arco-layout-content') as HTMLElement | null
  await Promise.all([loadPicks(), loadCfgLabel(), loadData(), pollEmbed()])
  loading.value = false
  void watchStartupScoring()
})

const toggle = async (g: ImageItem, c: Cand) => {
  const code = g.imageCode
  if (picks.value[code] === c.file) {
    delete picks.value[code]
    picks.value = { ...picks.value }
    await fetch(`/api/picks/${code}`, { method: 'DELETE' })
  } else {
    picks.value = { ...picks.value, [code]: c.file }
    await fetch(`/api/picks/${code}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ file: c.file }),
    })
  }
}

const clearAll = async () => {
  await fetch('/api/picks', { method: 'DELETE' })
  picks.value = {}
  Message.info('已清空')
}

const exportCsv = async () => {
  try {
    const resp = await fetch('/api/export')
    if (!resp.ok) { Message.error('导出失败：' + (await resp.text()).slice(0, 80)); return }
    const content = await resp.text()
    const d = new Date(), p2 = (n: number) => String(n).padStart(2, '0')
    const name = `已选图标-${d.getFullYear()}${p2(d.getMonth() + 1)}${p2(d.getDate())}${p2(d.getHours())}${p2(d.getMinutes())}${p2(d.getSeconds())}.csv`
    const avalonia = (window as any).invokeCSharpAction
    if (typeof avalonia === 'function') {
      avalonia(JSON.stringify({ name, content }))
      Message.success('已保存到下载目录')
      return
    }
    const bridge = (window as any).webkit?.messageHandlers?.saveFile
    if (bridge) {
      bridge.postMessage({ name, content })
      Message.success('已保存到下载目录')
      return
    }
    const url = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url; a.download = name; a.click()
    URL.revokeObjectURL(url)
  } catch (e) {
    Message.error('导出失败：' + (e as Error).message)
  }
}

const moveOpen = ref(false)
const moveTarget = ref('')
const moveRunning = ref(false)
const moveResult = ref<{ targetDir: string; moved: number; skipped: number; items: { code: string; file: string; status: string; reason?: string; target?: string }[] } | null>(null)
const now = new Date()
const p2 = (n: number) => String(n).padStart(2, "0")
const todayStamp = `${now.getFullYear()}${p2(now.getMonth() + 1)}${p2(now.getDate())}${p2(now.getHours())}${p2(now.getMinutes())}${p2(now.getSeconds())}`
const defaultMoveDir = '~/Desktop/' + todayStamp
const previewOpen = ref(false)
const previewSrc = ref('')
const openPreview = (src: string) => { previewSrc.value = src; previewOpen.value = true }

const openMove = () => {
  if (!counts.value.picked) return
  moveTarget.value = ''
  moveResult.value = null
  moveOpen.value = true
}
const closeAndAnalyze = async () => {
  moveOpen.value = false
  await analyze()
}
// ---------- 目录浏览器（后端 /api/fs/dirs，点选文件夹选路径） ----------
const fsOpen = ref(false)
const fsPath = ref(''), fsParent = ref(''), fsDirs = ref<string[]>([])
const fsShowAll = ref(false)
const fsTarget = ref<'move' | 'liveDir' | 'candDir'>('move')
const loadFs = async (p: string) => {
  const j = await (await fetch(`/api/fs/dirs?path=${encodeURIComponent(p)}&all=${fsShowAll.value}`)).json()
  if (j.error) { Message.error(j.error); return }
  fsPath.value = j.path; fsParent.value = j.parent ?? ''; fsDirs.value = j.dirs ?? []
}
const openFs = (target: 'move' | 'liveDir' | 'candDir') => {
  fsTarget.value = target; fsOpen.value = true; loadFs('')
}
const enterFs = (name: string) => loadFs(fsPath.value === '/' ? '/' + name : fsPath.value + '/' + name)
const upFs = () => { if (fsParent.value) loadFs(fsParent.value) }
const pickFs = () => {
  const p = fsPath.value
  if (fsTarget.value === 'move') moveTarget.value = p
  else if (fsTarget.value === 'liveDir' && !cfg.value.liveDirs.includes(p)) cfg.value.liveDirs.push(p)
  else if (fsTarget.value === 'candDir' && !cfg.value.candidateDirs.includes(p)) cfg.value.candidateDirs.push(p)
  fsOpen.value = false
}

const runMove = async () => {
  moveRunning.value = true
  try {
    const resp = await fetch("/api/adopt/move", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targetDir: moveTarget.value || null }),
    })
    const res = await resp.json()
    moveResult.value = res
    if (res.moved > 0) await analyze()
  } finally {
    moveRunning.value = false
  }
}
const pct = (n: number) => (n * 100).toFixed(1) + '%'
const f2 = (n: number | null) => (n == null ? '—' : n.toFixed(2))
const fmtTime = (s: string) => {
  const d = new Date(s)
  if (Number.isNaN(d.getTime())) return s
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

// ---------- 设置 ----------
interface PrefixItem { url: string; name: string }
interface AppConfig {
  candidateDirs: string[]
  liveDirs: string[]
  livePrefixes: PrefixItem[]
  defaultPrefixIndex: number
  livePathPattern: string
  modelUrl: string
  ocrModelUrl: string
  ocrModelPreset: string
  ocrModel: string
}
const cfgOpen = ref(false)
const cfg = ref<AppConfig>({ candidateDirs: [], liveDirs: [], livePrefixes: [], defaultPrefixIndex: 0, livePathPattern: 'long/{code}.jpg', modelUrl: '', ocrModelUrl: '', ocrModelPreset: 'latin', ocrModel: 'latin' })
const newCand = ref(''), newLive = ref(''), newPrefix = ref(''), newPrefixName = ref('')
const defaultPrefixLabel = ref('')
const pathPattern = ref('long/{code}.jpg')

const embedStatus = ref<{ running: boolean; done: number; total: number; error: string | null; startedAt?: string; finishedAt?: string | null; vectors: number; updatedAt?: string | null }>({ running: false, done: 0, total: 0, error: null, finishedAt: null, vectors: 0 })
let embedTimer: number | undefined
const pollEmbed = async () => {
  embedStatus.value = await (await fetch('/api/embed/status')).json()
  if (embedStatus.value.running) {
    if (!embedTimer) embedTimer = window.setInterval(pollEmbed, 1000)
  } else if (embedTimer) { window.clearInterval(embedTimer); embedTimer = undefined }
}
const embedPct = computed(() => embedStatus.value.total > 0 ? Math.min(1, embedStatus.value.done / embedStatus.value.total) : 0)
interface ModelStatus { running: boolean; done: number; total: number; error: string | null; modelExists: boolean; url?: string }
const modelStatus = ref<ModelStatus>({ running: false, done: 0, total: 0, error: null, modelExists: true })
const modelPct = computed(() => modelStatus.value.total > 0 ? Math.min(1, modelStatus.value.done / modelStatus.value.total) : 0)
interface OcrStatus { running: boolean; done: number; total: number; error: string | null; count: number; updatedAt?: string | null }
const ocrStatus = ref<OcrStatus>({ running: false, done: 0, total: 0, error: null, count: 0 })
const ocrPct = computed(() => ocrStatus.value.total > 0 ? Math.min(1, ocrStatus.value.done / ocrStatus.value.total) : 0)
const loadOcrStatus = async () => {
  try { ocrStatus.value = await (await fetch('/api/ocr/status')).json() } catch { /* 忽略 */ }
}
interface OcrModelStatus { downloading: boolean; dlDone?: number; dlTotal?: number; dlError?: string | null; modelReady: boolean; url?: string }
const ocrModel = ref<{ downloading: boolean; done: number; total: number; error: string | null; modelReady: boolean }>({ downloading: false, done: 0, total: 0, error: null, modelReady: true })
const ocrModelOptions = [
  { label: 'PP-OCRv5 Latin（默认）', value: 'latin' },
  { label: 'PP-OCRv5 中文', value: 'ch' },
  { label: 'PP-OCRv5 英文', value: 'en' },
]
const ocrModelPct = computed(() => ocrModel.value.total > 0 ? Math.min(1, ocrModel.value.done / ocrModel.value.total) : 0)
const loadOcrModel = async () => {
  try {
    const st: OcrModelStatus = await (await fetch('/api/ocr/model/status')).json()
    ocrModel.value = {
      downloading: st.downloading, done: st.dlDone ?? 0, total: st.dlTotal ?? 0,
      error: st.dlError ?? null, modelReady: st.modelReady,
    }
  } catch { /* 忽略 */ }
}
const downloadOcrModel = async () => {
  if (ocrModel.value.downloading) return
  const resp = await fetch('/api/ocr/model/download', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ model: cfg.value.ocrModel }),
  })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '下载启动失败'); return }
  while (true) {
    await loadOcrModel()
    if (!ocrModel.value.downloading) break
    await new Promise((r) => setTimeout(r, 1000))
  }
  if (ocrModel.value.error) Message.error('OCR 模型下载失败：' + ocrModel.value.error)
  else Message.success('OCR 模型下载完成')
}

const rebuildOcr = async (force: boolean) => {
  if (ocrStatus.value.running) return
  const resp = await fetch('/api/ocr/rebuild?force=' + force, { method: 'POST' })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '启动失败'); return }
  while (true) {
    await loadOcrStatus()
    if (!ocrStatus.value.running) break
    await new Promise((r) => setTimeout(r, 1000))
  }
  if (ocrStatus.value.error) Message.error('OCR 失败：' + ocrStatus.value.error)
  else Message.success(`OCR 完成，共 ${ocrStatus.value.count} 张`)
}

const loadModelStatus = async () => {
  try { modelStatus.value = await (await fetch('/api/model/status')).json() } catch { /* 忽略 */ }
}
const downloadModel = async () => {
  if (modelStatus.value.running) return
  const resp = await fetch('/api/model/download', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url: cfg.value.modelUrl }),
  })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '下载启动失败'); return }
  while (true) {
    await loadModelStatus()
    if (!modelStatus.value.running) break
    await new Promise((r) => setTimeout(r, 1000))
  }
  if (modelStatus.value.error) { Message.error('模型下载失败：' + modelStatus.value.error); return }
  Message.success('模型下载完成')
  await pollEmbed()
}

const rebuildVectors = async (force: boolean) => {
  const resp = await fetch(`/api/embed/rebuild?force=${force}`, { method: 'POST' })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '启动失败'); return }
  Message.info(force ? '全量重建已启动' : '增量重建已启动')
  await pollEmbed()
}

const openCfg = async () => {
  // 先并行取完数据，再一次性赋值，避免连锁触发整页重渲染
  const [c, e, m, o, om] = await Promise.all([
    fetch('/api/config').then((r) => r.json()),
    fetch('/api/embed/status').then((r) => r.json()),
    fetch('/api/model/status').then((r) => r.json()).catch(() => null),
    fetch('/api/ocr/status').then((r) => r.json()).catch(() => null),
    fetch('/api/ocr/model/status').then((r) => r.json()).catch(() => null),
  ])
  cfg.value = c
  embedStatus.value = e
  if (m) modelStatus.value = m
  if (o) ocrStatus.value = o
  if (om) ocrModel.value = { downloading: om.downloading, done: om.dlDone ?? 0, total: om.dlTotal ?? 0, error: om.dlError ?? null, modelReady: om.modelReady }
  newCand.value = newLive.value = newPrefix.value = newPrefixName.value = ''
  if (embedStatus.value.running && !embedTimer) embedTimer = window.setInterval(pollEmbed, 1000)
  cfgOpen.value = true
}
const loadCfgLabel = async () => {
  const c: AppConfig = await (await fetch('/api/config')).json()
  const p = c.livePrefixes[c.defaultPrefixIndex] ?? c.livePrefixes[0]
  defaultPrefixLabel.value = p ? (p.name || p.url) : ''
  pathPattern.value = c.livePathPattern || 'long/{code}.jpg'
}
const pathOf = (code: string) => (pathPattern.value || 'long/{code}.jpg').replace(/\{code\}/gi, code)
const addDir = (list: string[], v: string) => {
  const p = v.trim()
  if (!p.startsWith('/')) { Message.warning('请输入绝对路径（以 / 开头）'); return }
  if (list.includes(p)) { Message.warning('目录已存在'); return }
  list.push(p)
}
const addPrefix = () => {
  const p = newPrefix.value.trim().replace(/\/+$/, '')
  if (!/^https?:\/\//.test(p)) { Message.warning('前缀需以 http(s):// 开头'); return }
  if (cfg.value.livePrefixes.some((x) => x.url === p)) { Message.warning('前缀已存在'); return }
  cfg.value.livePrefixes.push({ url: p, name: newPrefixName.value.trim() || '未命名' })
  newPrefix.value = newPrefixName.value = ''
}
const move = <T,>(list: T[], i: number, d: -1 | 1) => {
  const j = i + d
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j], list[i]]
}
const saveCfg = async () => {
  const resp = await fetch('/api/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cfg.value),
  })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '保存失败'); return }
  cfg.value = await resp.json()
  pathPattern.value = cfg.value.livePathPattern || 'long/{code}.jpg'
  cfgOpen.value = false
  Message.success('配置已保存并生效')
}
</script>

<template>
  <a-layout class="app-layout" style="background: var(--color-bg-1)">
    <a-layout-header class="hdr">
      <div class="hdr-in">
        <div class="title-row">
          <a-space size="medium">
            <a-typography-title :heading="5" style="margin: 0">图标筛选工作台</a-typography-title>
        <a-space v-if="analyzing && analyzeProg" size="small" style="margin-left: 12px">
          <a-spin :size="14" />
          <a-typography-text type="secondary" style="font-size: 12px">{{ analyzeProg.phase }} {{ analyzeProg.done }}/{{ analyzeProg.total }}</a-typography-text>
        </a-space>
            <a-tag color="gray" size="small">排序图片 · 待补 {{ counts.all }}</a-tag>
          </a-space>
          <a-space size="small" class="hdr-actions">
            <a-button size="mini" :loading="analyzing" @click="analyze">重新分析</a-button>
            <a-radio-group v-model:model-value="theme" type="button" size="mini">
              <a-radio value="light">亮色</a-radio>
              <a-radio value="dark">暗色</a-radio>
            </a-radio-group>
            <a-button size="small" @click="openCfg">配置</a-button>
          </a-space>
        </div>
        <a-typography-text type="secondary" class="sub">
          <template v-if="defaultPrefixLabel">线上现图取自 {{ defaultPrefixLabel }} · </template>
          候选池 {{ data?.poolCount ?? '—' }} 张 · 向量 {{ embedStatus.vectors }} · 综合分 = 图像 64% + 文字 36%（无文字时归一）
        </a-typography-text>
        <div class="bar">
          <a-radio-group v-model:model-value="filter" type="button" size="small">
            <a-radio value="all">全部 {{ counts.all }}</a-radio>
            <a-radio value="same">同图 {{ counts.same }}</a-radio>
            <a-radio value="high">高相似 {{ counts.high }}</a-radio>
            <a-radio value="mid">中相似 {{ counts.mid }}</a-radio>
            <a-radio value="low">低 {{ counts.low }}</a-radio>
            <a-radio value="pending">计算中 {{ counts.pending }}</a-radio>
            <a-radio value="picked">已选 {{ counts.picked }}</a-radio>
          </a-radio-group>
          <div class="spacer" />
          <a-space size="small">
            <a-tag color="arcoblue">已选 {{ counts.picked }}</a-tag>
            <a-popconfirm content="清空所有选择？" @ok="clearAll">
              <a-button size="small" :disabled="!counts.picked">清空</a-button>
            </a-popconfirm>
            <a-button type="primary" size="small" :disabled="!counts.picked" @click="exportCsv">
              导出选择 CSV
            </a-button>
            <a-button size="small" :disabled="!counts.picked" @click="openMove">
              落盘移动
            </a-button>
          </a-space>
        </div>
      </div>
    </a-layout-header>

    <a-layout-content class="content">
      <div class="page">
        <a-spin :loading="loading" style="display: block; width: 100%">
          <div v-if="visible.length" class="vlist" :style="{ height: vTotal + 'px' }">
            <div
              v-for="row in vRows" :key="row.imageCode" :data-index="row.index" :ref="measureRow"
              class="vrow" :style="{ transform: `translateY(${row.start}px)` }"
            >
              <a-card class="gcard" :class="[tierOf(row.g), { picked: !!picks[row.g.imageCode] }]">
                <template #title>
                  <div class="card-title">
                    <a-space size="small">
                      <a-typography-text bold class="code">{{ row.g.imageCode }}</a-typography-text>
                        <a-tag size="small" :color="tierColor[tierOf(row.g)]">{{ tierName[tierOf(row.g)] }}</a-tag>
                    </a-space>
                    <a-tag v-if="picks[row.g.imageCode]" size="small" color="arcoblue" class="picked-tag">
                      已选 → {{ pathOf(row.g.imageCode) }}
                    </a-tag>
                  </div>
                </template>
                <a-row :gutter="16">
                  <a-col :span="4" :md="5" class="live-col">
                    <div class="livecol">
                      <div class="colhead livehead">线上现图</div>
                      <div class="thumb live-thumb">
                        <img
                          v-if="row.g.hasLive" class="live-img" :src="`/img/live/${row.g.imageCode}.jpg`" :alt="row.g.imageCode"
                          loading="lazy" decoding="async" @click="openPreview(`/img/live/${row.g.imageCode}.jpg`)"
                        />
                        <a-empty v-else description="缺失" style="padding: 24px 0" />
                      </div>
                      <a-typography-text type="secondary" class="path">{{ row.g.imageCode }}.jpg</a-typography-text>
                    </div>
                  </a-col>
                  <a-col :span="20" :md="19">
                    <div class="colhead">候选图标 Top {{ row.g.top.length }} · 点击卡片采用，再点取消</div>
                    <div v-if="row.g.pending" class="empty">候选计算中…</div>
                    <div v-else class="cands">
                      <div
                        v-for="(c, k) in row.g.top" :key="c.file"
                        class="cand" :class="[candTier(c), { on: picks[row.g.imageCode] === c.file }]"
                        @click="toggle(row.g, c)"
                      >
                        <div class="rank-row">
                          <span class="rank-no">#{{ k + 1 }}</span>
                          <span class="score" :class="candTier(c)">{{ pct(c.score) }}</span>
                          <span v-if="c.pixel != null && c.pixel >= SAME" class="same-chip">同图</span>
                          <span v-if="picks[row.g.imageCode] === c.file" class="check-chip">✓ 已选</span>
                        </div>
                        <div class="thumb">
                          <img :src="`/img/cand/${encodeURIComponent(c.file)}`" :alt="c.file" loading="lazy" decoding="async">
                        </div>
                          <div class="cand-file">{{ c.file }}</div>
                        <div class="factors">
                          <span class="fx" title="图像结构因子">图 {{ c.img.toFixed(2) }}</span>
                          <span class="fx" :class="{ na: c.text == null }" title="图上文字因子（OCR）">文 {{ f2(c.text) }}</span>
                        </div>
                        <div class="corr">{{ c.corr == null ? `clip 余弦 ${c.img.toFixed(2)}` : `结构 corr ${c.corr.toFixed(2)}` }}</div>
                      </div>
                    </div>
                  </a-col>
                </a-row>
              </a-card>
            </div>
          </div>
          <a-empty v-if="!visible.length && !loading" description="没有符合条件的项目" style="padding: 80px 0" />
        </a-spin>
      </div>
    </a-layout-content>

    <a-drawer v-model:visible="cfgOpen" title="工作台配置" :width="620" unmount-on-close :footer="false">
      <a-space direction="vertical" fill style="width: 100%" :size="20">
        <a-card title="线上现图前缀（按顺序尝试，默认优先）" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="6">
            <div v-for="(p, i) in cfg.livePrefixes" :key="p.url" class="cfg-item">
              <div class="cfg-row">
                <a-radio :model-value="cfg.defaultPrefixIndex === i" @change="cfg.defaultPrefixIndex = i">默认</a-radio>
                <a-input v-model="p.name" placeholder="别名" size="small" style="width: 130px" allow-clear />
                <div class="spacer" />
                <a-space size="mini">
                  <a-button size="mini" :disabled="i === 0" @click="move(cfg.livePrefixes, i, -1)">↑</a-button>
                  <a-button size="mini" :disabled="i === cfg.livePrefixes.length - 1" @click="move(cfg.livePrefixes, i, 1)">↓</a-button>
                  <a-button size="mini" status="danger" @click="cfg.livePrefixes.splice(i, 1)">删</a-button>
                </a-space>
              </div>
              <a-typography-text class="cfg-text" code :title="p.url">{{ p.url }}</a-typography-text>
            </div>
            <a-input-group class="cfg-add cfg-add-prefix">
              <a-input v-model="newPrefixName" placeholder="别名，如 正式环境" size="small" allow-clear style="width: 130px" />
              <a-input v-model="newPrefix" placeholder="https://static.example.com" allow-clear @keydown.enter="addPrefix" />
              <a-button type="primary" size="small" @click="addPrefix">添加前缀</a-button>
            </a-input-group>
          </a-space>
        </a-card>

        <a-card title="线上现图路径规则" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="6">
            <a-input v-model="cfg.livePathPattern" placeholder="long/{code}.jpg" allow-clear />
            <a-typography-text type="secondary" style="font-size: 11px">
              {code} 会替换为图片编码；用于按前缀远程取线上现图
            </a-typography-text>
          </a-space>
        </a-card>

                <a-card title="线上现图目录" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="6">
            <div v-for="(d, i) in cfg.liveDirs" :key="d" class="cfg-item">
              <div class="cfg-row">
                <a-typography-text class="cfg-text" code :title="d">{{ d }}</a-typography-text>
                <a-space size="mini">
                  <a-button size="mini" :disabled="i === 0" @click="move(cfg.liveDirs, i, -1)">↑</a-button>
                  <a-button size="mini" :disabled="i === cfg.liveDirs.length - 1" @click="move(cfg.liveDirs, i, 1)">↓</a-button>
                  <a-button size="mini" status="danger" @click="cfg.liveDirs.splice(i, 1)">删</a-button>
                </a-space>
              </div>
            </div>
            
              <a-input-group class="cfg-add">
              <a-input v-model="newLive" placeholder="绝对路径，如 /Users/xxx/图标目录" allow-clear @keydown.enter="addDir(cfg.liveDirs, newLive)" />
              <a-button @click="openFs('liveDir')">浏览</a-button>
              <a-button type="primary" @click="addDir(cfg.liveDirs, newLive)">添加目录</a-button>
            </a-input-group>
          </a-space>
        </a-card>

        <a-card title="候选图目录" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="6">
            <div v-for="(d, i) in cfg.candidateDirs" :key="d" class="cfg-item">
              <div class="cfg-row">
                <a-typography-text class="cfg-text" code :title="d">{{ d }}</a-typography-text>
                <a-space size="mini">
                  <a-button size="mini" :disabled="i === 0" @click="move(cfg.candidateDirs, i, -1)">↑</a-button>
                  <a-button size="mini" :disabled="i === cfg.candidateDirs.length - 1" @click="move(cfg.candidateDirs, i, 1)">↓</a-button>
                  <a-button size="mini" status="danger" :disabled="cfg.candidateDirs.length <= 1" @click="cfg.candidateDirs.splice(i, 1)">删</a-button>
                </a-space>
              </div>
            </div>
            
              <a-input-group class="cfg-add">
              <a-input v-model="newCand" placeholder="绝对路径，如 /Users/xxx/未使用图标" allow-clear @keydown.enter="addDir(cfg.candidateDirs, newCand)" />
              <a-button @click="openFs('candDir')">浏览</a-button>
              <a-button type="primary" @click="addDir(cfg.candidateDirs, newCand)">添加目录</a-button>
            </a-input-group>
          </a-space>
        </a-card>

        <a-card title="图像向量（CLIP）" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="8">
            <a-space size="small" wrap>
              <a-tag :color="modelStatus.modelExists ? 'green' : 'orange'">{{ modelStatus.modelExists ? '模型就绪' : '模型未下载' }}</a-tag>
              <a-tag v-if="modelStatus.running" color="arcoblue">下载中 {{ (modelPct * 100).toFixed(1) }}%</a-tag>
            </a-space>
            <a-progress v-if="modelStatus.running && modelStatus.total > 0" :percent="modelPct" size="small">
              <template #text="{ percent }">{{ (percent * 100).toFixed(1) }}%</template>
            </a-progress>
            <a-input-group class="cfg-add">
              <a-input v-model="cfg.modelUrl" placeholder="模型下载地址（http/https）" allow-clear />
              <a-button type="primary" :loading="modelStatus.running" :disabled="modelStatus.running" @click="downloadModel">下载模型</a-button>
            </a-input-group>
            <a-space size="small" wrap>
              <a-tag color="arcoblue">向量库 {{ embedStatus.vectors }}</a-tag>
              <a-tag v-if="embedStatus.running" color="orange">本次 {{ embedStatus.done }}/{{ embedStatus.total }}</a-tag>
              <a-tag v-else-if="embedStatus.error" color="red">出错：{{ embedStatus.error }}</a-tag>
              <a-tag v-else-if="embedStatus.updatedAt" color="green">
                上次重建 {{ fmtTime(embedStatus.updatedAt) }}<template v-if="embedStatus.total > 0"> · {{ embedStatus.done }}/{{ embedStatus.total }}</template>
              </a-tag>
            </a-space>
            <a-progress
              v-if="embedStatus.running && embedStatus.total > 0"
              :percent="embedPct" size="small"
            >
              <template #text="{ percent }">{{ (percent * 100).toFixed(2) }}%</template>
            </a-progress>
            <a-space size="small">
              <a-button size="small" :disabled="embedStatus.running || !modelStatus.modelExists" @click="rebuildVectors(false)">增量重建</a-button>
              <a-popconfirm content="全量重算所有向量（约 1 分钟）？" @ok="rebuildVectors(true)">
                <a-button size="small" status="warning" :disabled="embedStatus.running || !modelStatus.modelExists">全量重建</a-button>
              </a-popconfirm>
            </a-space>
            <a-typography-text type="secondary" style="font-size: 11px">
              模型约 336MB，保存在运行期数据目录 · 首次使用先下载；保存目录/前缀后建议增量重建
            </a-typography-text>
          </a-space>
        </a-card>

        <a-card title="图上文字（OCR）" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="8">
            <a-space size="small" wrap>
              <a-tag :color="ocrModel.modelReady ? 'green' : 'orange'">{{ ocrModel.modelReady ? '模型就绪' : '模型未下载' }}</a-tag>
              <a-tag v-if="ocrModel.downloading" color="arcoblue">下载中 {{ ocrModel.done }}/{{ ocrModel.total }}</a-tag>
            </a-space>
            <a-progress v-if="ocrModel.downloading && ocrModel.total > 0" :percent="ocrModelPct" size="small">
              <template #text="{ percent }">{{ (percent * 100).toFixed(0) }}%</template>
            </a-progress>
            <a-input-group class="cfg-add">
              <a-select
                v-model="cfg.ocrModel" :options="ocrModelOptions"
                placeholder="识别模型（可选择或输入地址）" allow-search allow-create allow-clear
              />
              <a-button type="primary" :loading="ocrModel.downloading" :disabled="ocrModel.downloading" @click="downloadOcrModel">下载模型</a-button>
            </a-input-group>
            <a-space size="small" wrap>
              <a-typography-text type="secondary" style="font-size: 11px">
                <a href="https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml" target="_blank">模型来源</a>
              </a-typography-text>
            </a-space>
            <a-space size="small" wrap>
              <a-tag color="arcoblue">已识别 {{ ocrStatus.count }} 张</a-tag>
              <a-tag v-if="ocrStatus.running" color="orange">本次 {{ ocrStatus.done }}/{{ ocrStatus.total }}</a-tag>
              <a-tag v-else-if="ocrStatus.error" color="red">出错：{{ ocrStatus.error }}</a-tag>
              <a-tag v-else-if="ocrStatus.updatedAt" color="green">上次识别 {{ fmtTime(ocrStatus.updatedAt) }}</a-tag>
            </a-space>
            <a-progress v-if="ocrStatus.running && ocrStatus.total > 0" :percent="ocrPct" size="small">
              <template #text="{ percent }">{{ (percent * 100).toFixed(1) }}%</template>
            </a-progress>
            <a-space size="small">
              <a-button size="small" :disabled="ocrStatus.running" @click="rebuildOcr(false)">增量识别</a-button>
              <a-popconfirm content="全量重新识别所有图片（可能较久）？" @ok="rebuildOcr(true)">
                <a-button size="small" status="warning" :disabled="ocrStatus.running">全量识别</a-button>
              </a-popconfirm>
            </a-space>
            <a-typography-text type="secondary" style="font-size: 11px">
              PaddleOCR PP-OCRv5（ONNX）· 识别结果用于文字因子
            </a-typography-text>
          </a-space>
        </a-card>

        <a-space>
          <a-button type="primary" @click="saveCfg">保存并生效</a-button>
          <a-button @click="cfgOpen = false">取消</a-button>
        </a-space>
      </a-space>
    </a-drawer>
  </a-layout>

  <a-modal v-model:visible="previewOpen" :footer="false" :width="720" title="线上现图" unmount-on-close>
    <div style="display: flex; justify-content: center; align-items: center; min-height: 120px">
      <img
        :src="previewSrc" alt=""
        style="max-width: 100%; max-height: 70vh; width: auto; height: auto; display: block; border-radius: 8px"
      />
    </div>
  </a-modal>

  <a-modal v-model:visible="moveOpen" title="点选落盘（重命名 + 转 JPEG + 移动）" :width="640" :mask-closable="false" unmount-on-close>
    <a-space direction="vertical" fill style="width: 100%" :size="12">
      <a-alert type="info">已选 {{ counts.picked }} 个 · 重命名为 {image_code}.jpg 并转 JPEG 后移动到目标目录，源文件删除</a-alert>
      <a-input-group class="cfg-add">
        <a-input v-model="moveTarget" :placeholder="defaultMoveDir + '（留空用默认）'" allow-clear />
        <a-button @click="openFs('move')">浏览</a-button>
      </a-input-group>
      <template v-if="moveResult">
        <a-space size="small">
          <a-tag color="green" size="large">成功 {{ moveResult.moved }}</a-tag>
          <a-tag v-if="moveResult.skipped" color="orange" size="large">跳过/失败 {{ moveResult.skipped }}</a-tag>
          <a-typography-text type="secondary" code style="font-size: 11px">{{ moveResult.targetDir }}</a-typography-text>
        </a-space>
        <div class="move-list" style="max-height: 260px; overflow: auto">
          <div v-for="it in moveResult.items" :key="it.code" style="font-size: 12px; padding: 2px 0">
            <a-tag size="small" :color="it.status === 'ok' ? 'green' : 'orange'">{{ it.status === 'ok' ? '成功' : it.status === 'skip' ? '跳过' : '失败' }}</a-tag>
            <span class="dim">{{ it.code }}</span>
            <span v-if="it.reason" class="dim">· {{ it.reason }}</span>
            <span v-if="it.target" class="dim" style="opacity:.7">→ {{ it.target }}</span>
          </div>
        </div>
        <a-alert v-if="moveResult.moved > 0" type="warning">候选文件已移出，关闭后自动重新分析以刷新列表</a-alert>
      </template>
    </a-space>
    <template #footer>
      <a-space>
        <a-button :disabled="moveRunning" @click="moveOpen = false">关闭</a-button>
        <a-button v-if="!moveResult" type="primary" :loading="moveRunning" :disabled="!counts.picked" @click="runMove">开始移动</a-button>
        <a-button v-else type="primary" :disabled="moveRunning" @click="closeAndAnalyze">关闭并重新分析</a-button>
      </a-space>
    </template>
  </a-modal>

  <a-modal v-model:visible="fsOpen" title="选择目录" :width="560" unmount-on-close>
    <a-space direction="vertical" fill style="width: 100%" :size="10">
      <div class="fs-path-row">
        <a-button size="mini" :disabled="!fsParent" @click="upFs">上级</a-button>
        <a-typography-text class="cfg-text" code :title="fsPath" style="flex: 1; min-width: 0">{{ fsPath }}</a-typography-text>
        <a-checkbox v-model:model-value="fsShowAll" @change="loadFs(fsPath)">显示隐藏目录</a-checkbox>
      </div>
      <div class="fs-list" style="max-height: 320px; overflow: auto; border: 1px solid var(--color-border-2); border-radius: 8px">
        <div
          v-for="d in fsDirs" :key="d"
          class="fs-dir" @click="enterFs(d)"
        ><IconFolder /> {{ d }}</div>
        <a-empty v-if="!fsDirs.length" description="无子目录" style="padding: 24px 0" />
      </div>
    </a-space>
    <template #footer>
      <a-space>
        <a-button @click="fsOpen = false">取消</a-button>
        <a-button type="primary" @click="pickFs">选此目录</a-button>
      </a-space>
    </template>
  </a-modal>
</template>

<style scoped>
.app-layout { height: 100vh; overflow: hidden; }
.content { flex: 1; min-height: 0; overflow-y: auto; background: var(--color-bg-1); scrollbar-color: var(--color-fill-3) var(--color-bg-1); }
.content::-webkit-scrollbar { width: 10px; }
.content::-webkit-scrollbar-track { background: var(--color-bg-1); }
.content::-webkit-scrollbar-thumb { background: var(--color-fill-3); border-radius: 5px; }
.content::-webkit-scrollbar-thumb:hover { background: var(--color-fill-4); }
.hdr {
  position: relative; z-index: 20; flex: none; height: auto; line-height: 1.5;
  padding: 14px 24px 12px; border-bottom: 1px solid var(--color-border-2);
  background: var(--color-bg-1);
}
.hdr-in { max-width: 1720px; margin: 0 auto; }
.title-row { display: flex; align-items: center; gap: 12px; margin-bottom: 4px; }
.hdr-actions { margin-left: auto; }
.sub { font-size: 12px; }
.bar { display: flex; gap: 12px; align-items: center; margin-top: 10px; row-gap: 8px; flex-wrap: wrap; }
.spacer { flex: 1; }
.page { max-width: 1720px; margin: 0 auto; padding: 16px 24px 48px; }
.vlist { position: relative; width: 100%; }
.vrow { position: absolute; top: 0; left: 0; width: 100%; box-sizing: border-box; padding-bottom: 16px; }

.gcard { border-radius: 12px; overflow: hidden; }
.gcard :deep(.arco-card-header) { border-bottom: 1px solid var(--color-border-1); padding: 12px 16px; }
.gcard :deep(.arco-card-body) { padding: 14px 16px; }
.card-title { display: flex; align-items: center; gap: 8px; }
.code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 14px; }
.picked-tag { margin-left: auto; margin-right: 12px; }
.gcard.picked { border-color: rgb(var(--primary-6)); box-shadow: 0 0 0 1px var(--color-primary-light-2) inset; }

.colhead {
  font-size: 11px; letter-spacing: .06em; color: var(--color-text-3);
  border-bottom: 1px solid var(--color-border-1); padding-bottom: 6px; margin-bottom: 10px;
  text-transform: uppercase;
}
.livecol {
  border: 1px solid var(--color-gold-6, #f59e0b); border-radius: 12px; padding: 10px;
  box-sizing: border-box; background: var(--color-bg-2); display: flex; flex-direction: column; gap: 8px; flex: 1;
  box-shadow: 0 0 0 3px rgba(245, 158, 11, .08);
}
.live-col { display: flex; }
.gcard :deep(.arco-row) { align-items: stretch; }
.thumb.live-thumb { aspect-ratio: auto; flex: 1; min-height: 0; }
.live-img { width: 100%; height: 100%; object-fit: cover; display: block; cursor: zoom-in; }
.livehead { color: var(--color-gold-6, #f59e0b); border-bottom-color: rgba(245, 158, 11, .3); }
.live-thumb { border-radius: 8px; overflow: hidden; border: 1px solid rgba(245, 158, 11, .35); }
.path { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; word-break: break-all; }


.cand {
  border: 1px solid var(--color-border-2); border-radius: 12px; background: var(--color-bg-2);
  box-sizing: border-box; padding: 10px; cursor: pointer; display: flex; flex-direction: column; gap: 8px; height: 100%; min-height: 0;
  transition: box-shadow .18s ease, border-color .18s ease, transform .18s ease, background-color .18s ease;
  position: relative;
}
.cand:hover { border-color: var(--color-border-3); transform: translateY(-2px); box-shadow: var(--shadow2-center); }
.rank-row { display: flex; align-items: center; gap: 6px; min-height: 22px; }
.rank-no { font-size: 12px; color: var(--color-text-3); font-weight: 600; }
.score { font-size: 13px; font-weight: 700; margin-left: auto; color: var(--color-text-2); }
.score.high { color: var(--color-success-6); }
.score.mid { color: var(--color-warning-6); }
.score.same { color: var(--color-pink-3); }
.same-chip, .check-chip {
  font-size: 10px; font-weight: 700; padding: 1px 7px; border-radius: 999px; line-height: 1.4;
}
.same-chip { color: var(--color-pink-3); border: 1px solid var(--color-pink-3); }
.check-chip { position: absolute; top: -8px; right: 8px; background: rgb(var(--primary-6)); color: #fff; box-shadow: var(--shadow1-center); }
.thumb {
  aspect-ratio: 208 / 280; border-radius: 8px; overflow: hidden;
  background: var(--color-fill-2); border: 1px solid var(--color-border-1);
}
.thumb img { width: 100%; height: 100%; object-fit: cover; display: block; }
.cand-name { font-size: 12px; font-weight: 600; color: var(--color-text-1); word-break: break-word; }
.cand-file {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 10.5px; color: var(--color-text-3); word-break: break-all; min-height: 14px;
}
.factors { display: flex; gap: 4px; flex-wrap: wrap; }
.fx {
  font-size: 10px; padding: 1px 7px; border-radius: 999px; line-height: 1.5;
  background: var(--color-fill-2); color: var(--color-text-2); border: 1px solid var(--color-border-1);
}
.fx.na { opacity: .45; }
.corr { font-size: 10.5px; color: var(--color-text-3); margin-top: auto; }
.cand.on { border-color: rgb(var(--primary-6)); background: var(--color-primary-light-1); box-shadow: 0 0 0 2px var(--color-primary-light-2); }
.cand.on .rank-no { color: rgb(var(--primary-6)); }

.cands { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 10px; }
.cand { min-width: 0; }
@media (max-width: 1100px) { .cands { grid-template-columns: repeat(3, minmax(0, 1fr)); } .page { padding: 12px 14px 40px; } }
@media (max-width: 720px) { .cands { grid-template-columns: repeat(2, minmax(0, 1fr)); } }

.cfg-row { display: flex; align-items: center; gap: 8px; width: 100%; }
.cfg-row .spacer { flex: 1; }
.cfg-item {
  box-sizing: border-box; width: 100%; max-width: 100%;
  border: 1px solid var(--color-border-1); border-radius: 8px;
  padding: 8px 10px; background: var(--color-fill-1);
  display: flex; flex-direction: column; gap: 6px;
}
.cfg-text {
  flex: 1; min-width: 0; font-size: 11px;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
  display: block; cursor: default;
}
.cfg-row .arco-space { flex: none; }
.fs-path-row { display: flex; align-items: center; gap: 8px; width: 100%; }
.fs-path-row .arco-checkbox { flex: none; white-space: nowrap; }
.cfg-add { display: flex; width: 100%; margin-top: 2px; align-items: stretch; }
.cfg-add > .arco-input-wrapper { flex: 1; min-width: 0; height: 32px; }
.cfg-add > .arco-btn { height: 32px; }
.cfg-add > .arco-select { flex: 1; min-width: 0; }
.cfg-add > .arco-select .arco-select-view { height: 32px; }
.cfg-add-prefix > .arco-input-wrapper:first-child { flex: 0 0 130px; }
.fs-dir { padding: 7px 14px; cursor: pointer; font-size: 13px; border-bottom: 1px solid var(--color-border-1); }
.fs-dir:hover { background: var(--color-fill-2); }
.fs-list, .move-list { scrollbar-color: var(--color-fill-3) var(--color-bg-1); }
.fs-list::-webkit-scrollbar, .move-list::-webkit-scrollbar { width: 8px; }
.fs-list::-webkit-scrollbar-track, .move-list::-webkit-scrollbar-track { background: var(--color-bg-1); }
.fs-list::-webkit-scrollbar-thumb, .move-list::-webkit-scrollbar-thumb { background: var(--color-fill-3); border-radius: 4px; }
.fs-list::-webkit-scrollbar-thumb:hover, .move-list::-webkit-scrollbar-thumb:hover { background: var(--color-fill-4); }
</style>
