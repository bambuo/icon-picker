<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconFolder } from '@arco-design/web-vue/es/icon'

interface Cand {
  file: string
  group: string
  name: string
  score: number
  img: number
  text: number | null
  nameSim: number
  corr: number | null
  pixel: number | null
}
interface Game {
  code: string
  hall: string
  name: string
  hasReal: boolean
  pending?: boolean
  top: Cand[]
}
interface Data {
  high: number
  mid: number
  sameImg: number
  poolCount: number
  results: Game[]
}

const data = ref<Data | null>(null)
const picks = ref<Record<string, string>>({})
const query = ref('')
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

watch(theme, (t) => {
  document.body.setAttribute('arco-theme', t)
  localStorage.setItem('icon-theme', t)
}, { immediate: true })

const SAME = computed(() => data.value?.sameImg ?? 0.78)
const HIGH = computed(() => data.value?.high ?? 0.75)
const MID = computed(() => data.value?.mid ?? 0.62)

const tierOf = (g: Game) => {
  if (g.pending) return 'pending'
  const b = g.top[0]
  if (!g.hasReal || !b) return 'none'
  if (b.pixel != null && b.pixel >= SAME.value) return 'same'
  if (b.score >= HIGH.value) return 'high'
  if (b.score >= MID.value) return 'mid'
  return 'low'
}
const tierName: Record<string, string> = {
  same: '同图', high: '可直接采用', mid: '建议目检', low: '低', none: '无真实图', pending: '计算中',
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
    if (picks.value[g.code]) c.picked++
  }
  return c
})

const visible = computed(() => {
  const q = query.value.trim().toLowerCase()
  return (data.value?.results ?? []).filter((g) => {
    const okF = filter.value === 'all' ? true
      : filter.value === 'picked' ? !!picks.value[g.code] : tierOf(g) === filter.value
    const okQ = !q || `${g.code} ${g.name} ${g.hall}`.toLowerCase().includes(q)
    return okF && okQ
  })
})

const loadPicks = async () => {
  picks.value = await (await fetch('/api/picks')).json()
}
onMounted(async () => {
  await Promise.all([loadPicks(), loadCfgLabel(), loadData(), pollEmbed()])
  loading.value = false
})

const toggle = async (g: Game, c: Cand) => {
  const code = g.code
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

const exportCsv = () => { window.location.href = '/api/export' }

const moveOpen = ref(false)
const moveTarget = ref('')
const moveRunning = ref(false)
const moveResult = ref<{ targetDir: string; moved: number; skipped: number; items: { code: string; file: string; status: string; reason?: string; target?: string }[] } | null>(null)
const now = new Date()
const p2 = (n: number) => String(n).padStart(2, "0")
const todayStamp = `${now.getFullYear()}${p2(now.getMonth() + 1)}${p2(now.getDate())}${p2(now.getHours())}${p2(now.getMinutes())}${p2(now.getSeconds())}`
const defaultMoveDir = '~/Desktop/' + todayStamp
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
const fsTarget = ref<'move' | 'realDir' | 'candDir'>('move')
const loadFs = async (p: string) => {
  const j = await (await fetch(`/api/fs/dirs?path=${encodeURIComponent(p)}&all=${fsShowAll.value}`)).json()
  if (j.error) { Message.error(j.error); return }
  fsPath.value = j.path; fsParent.value = j.parent ?? ''; fsDirs.value = j.dirs ?? []
}
const openFs = (target: 'move' | 'realDir' | 'candDir') => {
  fsTarget.value = target; fsOpen.value = true; loadFs('')
}
const enterFs = (name: string) => loadFs(fsPath.value === '/' ? '/' + name : fsPath.value + '/' + name)
const upFs = () => { if (fsParent.value) loadFs(fsParent.value) }
const pickFs = () => {
  const p = fsPath.value
  if (fsTarget.value === 'move') moveTarget.value = p
  else if (fsTarget.value === 'realDir' && !cfg.value.realDirs.includes(p)) cfg.value.realDirs.push(p)
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
    moveResult.value = await resp.json()
    if (moveResult.value.moved > 0) await analyze()
  } finally {
    moveRunning.value = false
  }
}
const pct = (n: number) => (n * 100).toFixed(1) + '%'
const f2 = (n: number | null) => (n == null ? '—' : n.toFixed(2))

// ---------- 设置 ----------
interface PrefixItem { url: string; name: string }
interface AppConfig {
  candidateDirs: string[]
  realDirs: string[]
  realPrefixes: PrefixItem[]
  defaultPrefixIndex: number
}
const cfgOpen = ref(false)
const cfg = ref<AppConfig>({ candidateDirs: [], realDirs: [], realPrefixes: [], defaultPrefixIndex: 0 })
const newCand = ref(''), newReal = ref(''), newPrefix = ref(''), newPrefixName = ref('')
const defaultPrefixLabel = ref('')

const embedStatus = ref<{ running: boolean; done: number; total: number; error: string | null; startedAt?: string; finishedAt?: string | null; vectors: number }>({ running: false, done: 0, total: 0, error: null, finishedAt: null, vectors: 0 })
let embedTimer: number | undefined
const pollEmbed = async () => {
  embedStatus.value = await (await fetch('/api/embed/status')).json()
  if (embedStatus.value.running) {
    if (!embedTimer) embedTimer = window.setInterval(pollEmbed, 1000)
  } else if (embedTimer) { window.clearInterval(embedTimer); embedTimer = undefined }
}
const embedPct = computed(() => embedStatus.value.total > 0 ? Math.min(1, embedStatus.value.done / embedStatus.value.total) : 0)

const rebuildVectors = async (force: boolean) => {
  const resp = await fetch(`/api/embed/rebuild?force=${force}`, { method: 'POST' })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '启动失败'); return }
  Message.info(force ? '全量重建已启动' : '增量重建已启动')
  await pollEmbed()
}

const openCfg = async () => {
  cfg.value = await (await fetch('/api/config')).json()
  newCand.value = newReal.value = newPrefix.value = newPrefixName.value = ''
  cfgOpen.value = true
  await pollEmbed()
}
const loadCfgLabel = async () => {
  const c: AppConfig = await (await fetch('/api/config')).json()
  const p = c.realPrefixes[c.defaultPrefixIndex] ?? c.realPrefixes[0]
  defaultPrefixLabel.value = p ? (p.name || p.url) : ''
}
const addDir = (list: string[], v: string) => {
  const p = v.trim()
  if (!p.startsWith('/')) { Message.warning('请输入绝对路径（以 / 开头）'); return }
  if (list.includes(p)) { Message.warning('目录已存在'); return }
  list.push(p)
}
const addPrefix = () => {
  const p = newPrefix.value.trim().replace(/\/+$/, '')
  if (!/^https?:\/\//.test(p)) { Message.warning('前缀需以 http(s):// 开头'); return }
  if (cfg.value.realPrefixes.some((x) => x.url === p)) { Message.warning('前缀已存在'); return }
  cfg.value.realPrefixes.push({ url: p, name: newPrefixName.value.trim() || '未命名' })
  newPrefix.value = newPrefixName.value = ''
}
const move = <T,>(list: T[], i: number, d: -1 | 1) => {
  const j = i + d
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j], list[i]]
}
const saveCfg = async () => {
  if (!cfg.value.candidateDirs.length) { Message.error('候选图目录不能为空'); return }
  if (!cfg.value.realPrefixes.length) { Message.error('至少保留一个真实图前缀'); return }
  const resp = await fetch('/api/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cfg.value),
  })
  if (!resp.ok) { Message.error((await resp.json()).error ?? '保存失败'); return }
  cfg.value = await resp.json()
  cfgOpen.value = false
  Message.success('配置已保存并生效')
}
</script>

<template>
  <a-layout style="min-height: 100vh; background: var(--color-bg-1)">
    <a-layout-header class="hdr">
      <div class="hdr-in">
        <div class="title-row">
          <a-space size="medium">
            <a-typography-title :heading="5" style="margin: 0">图标筛选工作台</a-typography-title>
        <a-space v-if="analyzing && analyzeProg" size="small" style="margin-left: 12px">
          <a-spin :size="14" />
          <a-typography-text type="secondary" style="font-size: 12px">{{ analyzeProg.phase }} {{ analyzeProg.done }}/{{ analyzeProg.total }}</a-typography-text>
        </a-space>
            <a-tag color="gray" size="small">排序游戏 · 待补 {{ counts.all }}</a-tag>
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
          真实图取自 {{ defaultPrefixLabel || "…" }} 现图 · 候选池 {{ data?.poolCount ?? '—' }} 张 · 向量 {{ embedStatus.vectors }} · 综合分 = 图像 45% + 名字 30% + 文字 25%
        </a-typography-text>
        <div class="bar">
          <a-radio-group v-model:model-value="filter" type="button" size="small">
            <a-radio value="all">全部 {{ counts.all }}</a-radio>
            <a-radio value="same">同图 {{ counts.same }}</a-radio>
            <a-radio value="high">可直接采用 {{ counts.high }}</a-radio>
            <a-radio value="mid">建议目检 {{ counts.mid }}</a-radio>
            <a-radio value="low">低 {{ counts.low }}</a-radio>
            <a-radio value="none">无真实图 {{ counts.none }}</a-radio>
          <a-radio value="pending">计算中 {{ counts.pending }}</a-radio>
            <a-radio value="picked">已选 {{ counts.picked }}</a-radio>
          </a-radio-group>
        <a-input-search
          v-model="query" placeholder="搜索 编码 / 名称 / 厂商" style="width: 260px"
          allow-clear size="small"
        />
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

    <a-layout-content>
      <div class="page">
        <a-spin :loading="loading" style="display: block; width: 100%">
          <a-space direction="vertical" fill style="width: 100%" :size="16">
            <a-card v-for="g in visible" :key="g.code" class="gcard" :class="[tierOf(g), { picked: !!picks[g.code] }]">
              <template #title>
                <div class="card-title">
                  <a-space size="small">
                    <a-typography-text bold class="code">{{ g.code }}</a-typography-text>
                    <a-typography-text type="secondary">{{ g.hall }} · {{ g.name }}</a-typography-text>
                    <a-tag size="small" :color="tierColor[tierOf(g)]">{{ tierName[tierOf(g)] }}</a-tag>
                  </a-space>
                  <a-tag v-if="picks[g.code]" size="small" color="arcoblue" class="picked-tag">
                    已选 → long/{{ g.code }}.jpg
                  </a-tag>
                </div>
              </template>
              <a-row :gutter="16">
                <a-col :span="4" :md="5">
                  <div class="realcol">
                    <div class="colhead realhead">真实图 · 线上现图</div>
                    <div class="thumb real-thumb">
                      <a-image
                        v-if="g.hasReal" :src="`/img/real/${g.code}.jpg`" :alt="g.name"
                        width="100%" height="100%" fit="cover" :preview="true"
                      />
                      <a-empty v-else description="缺失" style="padding: 24px 0" />
                    </div>
                    <a-typography-text type="secondary" class="path">{{ g.code }}.jpg</a-typography-text>
                  </div>
                </a-col>
                <a-col :span="20" :md="19">
                  <div class="colhead">候选图标 Top {{ g.top.length }} · 点击卡片采用，再点取消</div>
                  <div v-if="g.pending" class="empty">候选计算中…</div>
                  <div v-else class="cands">
                    <div
                      v-for="(c, k) in g.top" :key="c.file"
                      class="cand" :class="[candTier(c), { on: picks[g.code] === c.file }]"
                      @click="toggle(g, c)"
                    >
                      <div class="rank-row">
                        <span class="rank-no">#{{ k + 1 }}</span>
                        <span class="score" :class="candTier(c)">{{ pct(c.score) }}</span>
                        <span v-if="c.pixel != null && c.pixel >= SAME" class="same-chip">同图</span>
                        <span v-if="picks[g.code] === c.file" class="check-chip">✓ 已选</span>
                      </div>
                      <div class="thumb">
                        <img :src="`/img/cand/${encodeURIComponent(c.file)}`" :alt="c.name" loading="lazy">
                      </div>
                      <div class="cand-name">{{ c.group }} · {{ c.name }}</div>
                      <div class="cand-file">{{ c.file }}</div>
                      <div class="factors">
                        <span class="fx" title="图像结构因子">图 {{ c.img.toFixed(2) }}</span>
                        <span class="fx" :class="{ na: c.text == null }" title="图上文字因子（OCR）">文 {{ f2(c.text) }}</span>
                        <span class="fx" title="名字因子">名 {{ c.nameSim.toFixed(2) }}</span>
                      </div>
                      <div class="corr">{{ c.corr == null ? `clip 余弦 ${c.img.toFixed(2)}` : `结构 corr ${c.corr.toFixed(2)}` }}</div>
                    </div>
                  </div>
                </a-col>
              </a-row>
            </a-card>
            <a-empty v-if="!visible.length && !loading" description="没有符合条件的项目" style="padding: 80px 0" />
          </a-space>
        </a-spin>
      </div>
    </a-layout-content>

    <a-drawer v-model:visible="cfgOpen" title="工作台配置" :width="620" unmount-on-close :footer="false">
      <a-space direction="vertical" fill style="width: 100%" :size="20">
        <a-card title="真实图前缀（按顺序尝试，默认优先）" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="6">
            <div v-for="(p, i) in cfg.realPrefixes" :key="p.url" class="cfg-item">
              <div class="cfg-row">
                <a-radio :model-value="cfg.defaultPrefixIndex === i" @change="cfg.defaultPrefixIndex = i">默认</a-radio>
                <a-input v-model="p.name" placeholder="别名" size="small" style="width: 130px" allow-clear />
                <div class="spacer" />
                <a-space size="mini">
                  <a-button size="mini" :disabled="i === 0" @click="move(cfg.realPrefixes, i, -1)">↑</a-button>
                  <a-button size="mini" :disabled="i === cfg.realPrefixes.length - 1" @click="move(cfg.realPrefixes, i, 1)">↓</a-button>
                  <a-button size="mini" status="danger" :disabled="cfg.realPrefixes.length <= 1" @click="cfg.realPrefixes.splice(i, 1)">删</a-button>
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

        <a-card title="真实图目录" size="small">
          <a-space direction="vertical" fill style="width: 100%" :size="6">
            <div v-for="(d, i) in cfg.realDirs" :key="d" class="cfg-item">
              <div class="cfg-row">
                <a-typography-text class="cfg-text" code :title="d">{{ d }}</a-typography-text>
                <a-space size="mini">
                  <a-button size="mini" :disabled="i === 0" @click="move(cfg.realDirs, i, -1)">↑</a-button>
                  <a-button size="mini" :disabled="i === cfg.realDirs.length - 1" @click="move(cfg.realDirs, i, 1)">↓</a-button>
                  <a-button size="mini" status="danger" @click="cfg.realDirs.splice(i, 1)">删</a-button>
                </a-space>
              </div>
            </div>
            
              <a-input-group class="cfg-add">
              <a-input v-model="newReal" placeholder="绝对路径，如 /Users/xxx/图标目录" allow-clear @keydown.enter="addDir(cfg.realDirs, newReal)" />
              <a-button @click="openFs('realDir')">浏览</a-button>
              <a-button type="primary" @click="addDir(cfg.realDirs, newReal)">添加目录</a-button>
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
              <a-tag color="arcoblue">向量库 {{ embedStatus.vectors }}</a-tag>
              <a-tag v-if="embedStatus.running" color="orange">本次 {{ embedStatus.done }}/{{ embedStatus.total }}</a-tag>
              <a-tag v-else-if="embedStatus.error" color="red">出错：{{ embedStatus.error }}</a-tag>
              <a-tag v-else-if="embedStatus.finishedAt" color="green">上次重建完成 {{ embedStatus.done }}/{{ embedStatus.total }}</a-tag>
            </a-space>
            <a-progress
              v-if="embedStatus.running && embedStatus.total > 0"
              :percent="embedPct" size="small"
            >
              <template #text="{ percent }">{{ (percent * 100).toFixed(2) }}%</template>
            </a-progress>
            <a-space size="small">
              <a-button size="small" :disabled="embedStatus.running" @click="rebuildVectors(false)">增量重建（新目录/新图）</a-button>
              <a-popconfirm content="全量重算所有向量（约 1 分钟）？" @ok="rebuildVectors(true)">
                <a-button size="small" status="warning" :disabled="embedStatus.running">全量重建</a-button>
              </a-popconfirm>
            </a-space>
            <a-typography-text type="secondary" style="font-size: 11px">
              模型 backend/models/vision_model.onnx · 保存目录/前缀后建议增量重建，向量变更即时影响图像因子排序
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

  <a-modal v-model:visible="moveOpen" title="点选落盘（重命名 + 转 JPEG + 移动）" :width="640" :mask-closable="false" unmount-on-close>
    <a-space direction="vertical" fill style="width: 100%" :size="12">
      <a-alert type="info">已选 {{ counts.picked }} 个 · 重命名为 {game_code}.jpg 并转 JPEG 后移动到目标目录，源文件删除</a-alert>
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
.hdr {
  position: sticky; top: 0; z-index: 20; height: auto; line-height: 1.5;
  padding: 14px 24px 12px; border-bottom: 1px solid var(--color-border-2);
  background: var(--color-bg-1);
}
.hdr-in { max-width: 1720px; margin: 0 auto; }
.title-row { display: flex; align-items: center; gap: 12px; margin-bottom: 4px; }
.hdr-actions { margin-left: auto; }
.sub { font-size: 12px; }
.bar { display: flex; gap: 12px; align-items: center; margin-top: 10px; row-gap: 8px; flex-wrap: wrap; }
.spacer { flex: 1; }
.page { max-width: 1720px; margin: 0 auto; padding: 16px 24px 48px; min-height: 40vh; }

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
.realcol {
  border: 1px solid var(--color-gold-6, #f59e0b); border-radius: 12px; padding: 10px;
  box-sizing: border-box; background: var(--color-bg-2); display: flex; flex-direction: column; gap: 8px; height: 100%;
  box-shadow: 0 0 0 3px rgba(245, 158, 11, .08);
}
.realhead { color: var(--color-gold-6, #f59e0b); border-bottom-color: rgba(245, 158, 11, .3); }
.real-thumb { border-radius: 8px; overflow: hidden; border: 1px solid rgba(245, 158, 11, .35); }
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
.cfg-add-prefix > .arco-input-wrapper:first-child { flex: 0 0 130px; }
.fs-dir { padding: 7px 14px; cursor: pointer; font-size: 13px; border-bottom: 1px solid var(--color-border-1); }
.fs-dir:hover { background: var(--color-fill-2); }
.fs-list, .move-list { scrollbar-color: var(--color-fill-3) transparent; }
.fs-list::-webkit-scrollbar, .move-list::-webkit-scrollbar { width: 8px; }
.fs-list::-webkit-scrollbar-track, .move-list::-webkit-scrollbar-track { background: transparent; }
.fs-list::-webkit-scrollbar-thumb, .move-list::-webkit-scrollbar-thumb { background: var(--color-fill-3); border-radius: 4px; }
.fs-list::-webkit-scrollbar-thumb:hover, .move-list::-webkit-scrollbar-thumb:hover { background: var(--color-fill-4); }
</style>
