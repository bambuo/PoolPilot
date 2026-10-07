<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'

import {
  api,
  type ExchangeSymbol,
  type SourceSymbol,
  type SymbolMapping,
  type SymbolMappingFailure,
  type SymbolPlatform,
} from '@/api/client'
import HeaderHint from '@/components/HeaderHint.vue'
import { formatNumber, formatSince } from '@/utils/format'

/// 标的映射 —— 跨交易所的"源标的 → 目标合约"对照表。
///
/// 它存在的理由很具体：**跟单员收到信号时要在毫秒级决定这单能不能下**，
/// 那次查询不能打网络。所以例外先备在本地表里（与源不同的那些），
/// 信号到达时只读一次表；表里没有的同名直接用，不必查。
///
/// 两条必须显示出来的信息：
/// <list type="number">
///   <item>**价格比值**。同名不等于同物——映射错了会把仓位开在另一个资产上，
///     而界面上看起来完全正常。比值接近 1 才说明两边确实是同一个东西；
///     它同时是"这条映射现在还可信吗"的唯一量化依据（隔段时间回来看一眼）。</item>
///   <item>**来源**。扫描出来的行会被下次扫描刷新，人工登记的行不会被覆盖——
///     人把某个标的指到哪是一个判断，机器没有资格推翻它。</item>
/// </list>
const mappings = ref<SymbolMapping[]>([])

/// 系统支持的平台（后端枚举的平台符号 + 有没有目录实现）。
/// **不是"你配了哪些交易所"**：映射是平台级的知识，没配任何配置时也该能看与整理。
const platforms = ref<SymbolPlatform[]>([])
const loading = ref(false)
const scanning = ref(false)
const errorMessage = ref('')
const scanResult = ref<string | null>(null)

/// 扫描时没能映射上的源标的（含原因）。与 scanResult 一起出现、一起被下一次扫描清掉。
const scanFailures = ref<SymbolMappingFailure[]>([])

/// 0 = 还没选。不用 null：Arco 的 select 的 v-model 不接受 null（类型检查会拦下），
/// 而"没有选中"这件事用一个哨兵值表达就够了。
const selectedPlatform = ref('')

/// "全部平台"的哨兵值：表格里既然有「交易所」列，就该能一眼看全所有平台的映射。
const AllPlatforms = '*'

const platform = computed(() =>
  platforms.value.find((p) => p.platform === selectedPlatform.value) ?? null,
)

/// 当前是不是"全部平台"视图。这时扫描/登记没有单一目标，按钮要禁用。
const isAllPlatforms = computed(() => selectedPlatform.value === AllPlatforms)

/// 这条配置的平台显示名（映射的「目标平台」就是它）。
const platformLabel = (code: string): string => {
  const labels: Record<string, string> = {
    binance: '币安',
    okx: 'OKX',
    gate: 'Gate',
    lighter: 'Lighter',
    custom: '其他',
  }

  return labels[code] ?? code
}

/// 这个平台能不能扫合约清单 / 登记映射。
///
/// 由后端按平台能力如实返回（`supported`），界面据此**提前禁用并写明原因**——
/// 而不是让人点一下再吃一个错误。"这家还不能扫"是使用者该在点之前就知道的事。
const canEditMappings = computed(() => !isAllPlatforms.value && platform.value?.supported === true)

const stats = computed(() => {
  const rows = mappings.value
  const enabled = rows.filter((r) => r.isEnabled)
  // 传统金融与否由后端按**各平台自己的口径**判定后带出来：
  // 前端再按合约类型字符串猜一次，OKX/Gate 的股票合约会被数成"加密"
  const tradfi = rows.filter((r) => r.targetIsTradFi).length
  const manual = rows.filter((r) => r.origin === 'manual').length
  const shaky = rows.filter((r) => r.priceRatio !== null && r.priceRatio > 1.2).length

  return { total: rows.length, enabled: enabled.length, tradfi, manual, shaky }
})

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    if (platforms.value.length === 0) {
      platforms.value = await api.symbolPlatforms()

      // 默认落在第一个**有目录实现**的平台上：那是最可能想看的那个
      selectedPlatform.value =
        platforms.value.find((p) => p.supported)?.platform ?? platforms.value[0]?.platform ?? ''
    }

    mappings.value = await api.symbolMappings(isAllPlatforms.value ? undefined : selectedPlatform.value)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

async function scan(): Promise<void> {
  if (!canEditMappings.value) return

  scanning.value = true
  errorMessage.value = ''
  scanResult.value = null
  scanFailures.value = []

  try {
    const result = await api.scanSymbolMappings(selectedPlatform.value)

    // direct 与 deleted 是同一批标的的两面：同名→直接用，其中早先被登记过的那几行顺带收掉。
    // 并列着写会读成两件事，所以 deleted 用"其中"挂在 direct 后面
    scanResult.value =
      `扫描了 ${result.sourceMarkets} 个源市场：同名直接用 ${result.direct}` +
      (result.deleted > 0 ? `（其中收掉早先的冗余行 ${result.deleted}）` : '') +
      `、新登记 ${result.created}、刷新 ${result.updated}` +
      (result.kept > 0 ? `、保留停用行 ${result.kept}` : '') +
      (result.skippedManual > 0 ? `、跳过人工登记 ${result.skippedManual}` : '')

    // 映射不上的逐条列出来。只报一个数字（"失败 2"）等于把"为什么"这件事丢回给使用者去猜,
    // 而原因（同名不同物 / 这家没有 / 取不到价）恰好是这张表最需要讲清的东西
    scanFailures.value = result.failures

    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    scanning.value = false
  }
}

// ── 登记 / 修正 ────────────────────────────────────────

const formOpen = ref(false)
const saving = ref(false)
const editingId = ref<number | null>(null)

/// 弹窗里**自己的**平台选择：默认跟页面上的选择器一致，改了只影响这一次登记。
/// 弹窗内报错而不是丢到页面顶部——弹窗可能还开着，错误必须出现在视线里。
const formPlatform = ref('')
const formError = ref('')

/// 源标的清单（Lighter 的全部市场）。
const sourceOptions = ref<SourceSymbol[]>([])
const sourceLoading = ref(false)
const sourceBase = ref('')

/// 源交易币固定 USDC：实测 Lighter 的逐市场 quote 字段恒为 0，只能按系统口径取它。
/// 界面上照样**显示出来**（只读）——标的的另一半不该是隐形的。
const SourceQuote = 'USDC'

const targetBase = ref('')
const targetQuote = ref('')
const isEnabled = ref(true)
const note = ref('')
const symbols = ref<ExchangeSymbol[]>([])
const symbolsLoading = ref(false)

async function openForm(row: SymbolMapping | null): Promise<void> {
  editingId.value = row?.id ?? null
  formError.value = ''

  // 平台默认取页面上选的那个（多半就是想要的）；全部平台视图下没有单一目标，取第一个支持的
  formPlatform.value =
    isAllPlatforms.value || selectedPlatform.value === ''
      ? platforms.value.find((p) => p.supported)?.platform ?? platforms.value[0]?.platform ?? ''
      : selectedPlatform.value

  if (!isAllPlatforms.value && selectedPlatform.value !== '' && selectedPlatform.value === formPlatform.value) {
    // 与页面选择一致时，直接沿用已经拉过的目标合约清单
    formPlatform.value = selectedPlatform.value
  }

  sourceBase.value = row?.sourceBase ?? ''
  targetBase.value = row?.targetBase ?? ''
  targetQuote.value = row?.targetQuote ?? ''
  isEnabled.value = row?.isEnabled ?? true
  note.value = row?.note ?? ''
  formOpen.value = true

  await loadFormOptions()
}

/// 按弹窗里选定的平台拉两份清单：Lighter 的源标的、该平台的目标合约。
/// 换平台要重新拉目标合约（各家的清单不同），源标的与平台无关，拉一次就够。
async function loadFormOptions(): Promise<void> {
  if (formPlatform.value === '') {
    return
  }

  if (sourceOptions.value.length === 0) {
    sourceLoading.value = true

    try {
      sourceOptions.value = await api.sourceSymbols()
    } catch (error) {
      formError.value = error instanceof Error ? error.message : String(error)
    } finally {
      sourceLoading.value = false
    }
  }

  symbolsLoading.value = true

  try {
    symbols.value = await api.platformSymbols(formPlatform.value)
    targetBase.value = ''
    targetQuote.value = ''
  } catch (error) {
    formError.value = error instanceof Error ? error.message : String(error)
    symbols.value = []
  } finally {
    symbolsLoading.value = false
  }
}

/// 所选资产币在该平台上的**报价币**（币安 U 本位都是 USDT，但模型上它是一段独立的字段，
/// 界面上就该显示成一段）。只有一种时自动选中，避免让人多点一次。
/// 去重后的资产币，供目标标的那一段选择用。
/// 币安的清单里一个资产币可能有多个合约（实测 SPCX 有 SPCXUSDT 与 SPCXUSD1），
/// 直接列会重复；交易币那一段单独选，所以这里按资产币去重并保留一条做展示。
const baseAssets = computed(() => {
  const seen = new Map<string, ExchangeSymbol>()

  for (const symbol of symbols.value) {
    if (!seen.has(symbol.baseAsset)) {
      seen.set(symbol.baseAsset, symbol)
    }
  }

  return [...seen.values()].sort((a, b) => a.baseAsset.localeCompare(b.baseAsset))
})

const targetQuotes = computed(() =>
  [...new Set(symbols.value.filter((s) => s.baseAsset === targetBase.value).map((s) => s.quoteAsset))],
)

watch(targetQuotes, (quotes) => {
  if (quotes.length === 1) {
    targetQuote.value = quotes[0] ?? ''
  } else if (!quotes.includes(targetQuote.value)) {
    targetQuote.value = ''
  }
})

/// 目标合约下拉的过滤：合约代码与基础资产都能搜（两种写法都会有人用）。
function filterSymbol(input: string, option: { label?: unknown; value?: unknown }): boolean {
  const keyword = input.trim().toLowerCase()
  if (keyword === '') return true

  const label = typeof option.label === 'string' ? option.label.toLowerCase() : ''
  const value = typeof option.value === 'string' ? option.value.toLowerCase() : ''

  return label.includes(keyword) || value.includes(keyword)
}

async function submit(): Promise<void> {
  // 缺字段要说出来，而且**说在弹窗里**：此前这里直接 return，点了「确定」什么都没发生，
  // 使用者只会以为按钮坏了（静默失败比报错难查得多）
  if (formPlatform.value === '') {
    formError.value = '先选目标交易所。'
    return
  }

  if (sourceBase.value.trim() === '' || targetBase.value === '' || targetQuote.value === '') {
    formError.value = '源标的与目标标的都要选：标的两段缺一不可。'
    return
  }

  saving.value = true
  formError.value = ''

  try {
    const current = mappings.value.find((m) => m.id === editingId.value)

    await api.saveSymbolMapping({
      targetPlatform: formPlatform.value,
      sourceBase: sourceBase.value.trim().toUpperCase(),
      targetBase: targetBase.value,
      targetQuote: targetQuote.value,
      isEnabled: isEnabled.value,
      ...(current?.sourcePrice == null ? {} : { sourcePrice: current.sourcePrice }),
      note: note.value.trim(),
    })

    formOpen.value = false
    await load()
  } catch (error) {
    // 失败时弹窗**不关**：错误与表单要同时看得见，否则用户只能靠重试猜哪里不对
    formError.value = error instanceof Error ? error.message : String(error)
  } finally {
    saving.value = false
  }
}

async function toggle(row: SymbolMapping): Promise<void> {
  errorMessage.value = ''

  try {
    await api.saveSymbolMapping({
      targetPlatform: row.targetPlatform,
      sourceBase: row.sourceBase,
      targetBase: row.targetBase,
      targetQuote: row.targetQuote,
      isEnabled: !row.isEnabled,
      ...(row.sourcePrice == null ? {} : { sourcePrice: row.sourcePrice }),
      ...(row.note == null ? {} : { note: row.note }),
    })

    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  }
}

const deleteTarget = ref<SymbolMapping | null>(null)
const deleteOpen = ref(false)

function requestDelete(row: SymbolMapping): void {
  deleteTarget.value = row
  deleteOpen.value = true
}

async function confirmDelete(): Promise<void> {
  if (deleteTarget.value === null) return

  try {
    await api.deleteSymbolMapping(deleteTarget.value.id)
    deleteOpen.value = false
    deleteTarget.value = null
    await load()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  }
}

/// 比值的颜色：越接近 1 越可信。>1.2 要留意，>3 基本不是同一个东西。
function ratioTone(ratio: number | null): string {
  if (ratio === null) return ''
  if (ratio <= 1.2) return 'rgb(var(--success-6))'
  if (ratio <= 3) return 'rgb(var(--warning-6))'

  return 'rgb(var(--danger-6))'
}

onMounted(load)
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>标的映射</span>
          <a-tag v-if="!loading" size="small">{{ stats.total }} 条</a-tag>
        </a-space>
      </template>
      <template #extra>
        <a-space>
          <!-- 选择器始终显示：这张表的范围就是"哪条交易所配置"。多平台时它是唯一的范围标识，
               所以标签里带上平台名；"全部交易所"让「交易所」列有意义 -->
          <!-- 选择器列的是**系统支持的平台**，不是你配了哪些交易所：
               映射是"哪个资产对应哪个合约"的知识，与你有几个账号无关 -->
          <a-select v-model="selectedPlatform" size="small" :style="{ width: '220px' }" @change="load">
            <a-option :value="AllPlatforms" label="全部平台">全部平台</a-option>
            <a-option
              v-for="p in platforms"
              :key="p.platform"
              :value="p.platform"
              :label="`${platformLabel(p.platform)}（${p.platform}）`"
            >
              {{ platformLabel(p.platform) }}
              <span class="mono" style="color: var(--color-text-3)">{{ p.platform }}</span>
              <span v-if="!p.supported"> · 暂无合约目录</span>
            </a-option>
          </a-select>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
          <a-button size="small" :loading="scanning" :disabled="!canEditMappings" @click="scan">
            扫描被观察的池
          </a-button>
          <a-button type="primary" size="small" :disabled="!canEditMappings" @click="openForm(null)">
            登记映射
          </a-button>
        </a-space>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <div class="hint-line">
        跟单员收到信号时先查这张表：<b>一次本地读，不打网络</b>；<b>表里没有的就同名直接用</b>。
        扫描会把被观察池里的市场逐个解析一遍，只把与源不同的写进来；
        <b>人工登记的行不会被扫描覆盖</b>——把它指到哪是一个判断，机器没有资格推翻。
        <template v-if="isAllPlatforms">
          <b>当前显示全部平台的映射</b>——每行的「交易所」列是平台符号；要扫描或登记，先选定一个平台。
        </template>
        <template v-else>
          <b>目标平台：{{ platformLabel(selectedPlatform) }}</b>
          <span class="mono">（{{ selectedPlatform }}）</span>。
          映射按<b>平台</b>存、不按账号：你在币安有几个账号都共用这一份。
        </template>
      </div>

      <div v-if="!isAllPlatforms && platform && !canEditMappings" class="hint-line" style="margin-top: 6px">
        <b style="color: rgb(var(--warning-6))">
          {{ platformLabel(selectedPlatform) }} 还没有合约目录实现，扫描与登记都用不了。
        </b>
        换到有目录的平台可以继续整理；这家等它的目录接上再说。
      </div>

      <!-- 这张表是**例外表**：同名的不在这里。把这条规则写在最显眼处——
           否则看到"表里只有一条"的人会以为扫描没干活 -->
      <div class="hint-line" style="margin-top: 6px">
        <b>这张表只放「与源不一样」的标的</b>：改了名的（<span class="mono">WTI</span> →
        <span class="mono">CL</span>）和人工作出的判断。<b>同名的不占一行</b>——
        <span class="mono">BTC</span> 在币安就是 <span class="mono">BTC/USDT</span>，
        跟单时直接用；为它登记一条只是把常识抄一遍。扫描会做这件事：同名的报个数、不写行，
        并收掉早先留下的同名冗余行。
      </div>

      <div class="hint-line" style="margin-top: 6px">
        <b>价格比值</b>是这张表最该盯的数：同名不等于同物，比值接近 1 才说明两边确实是同一个东西
        （实测 Lighter 的 <span class="mono">US500</span> 与币安 <span class="mono">SPYUSDT</span>
        差 10 倍，是两个不同工具；而两边的 <span class="mono">SPX</span> 都是 0.43，同名同物）。
        {{ stats.tradfi }} 条走传统金融合约（股票/商品/指数），{{ stats.manual }} 条为人工登记。
        <template v-if="stats.shaky > 0">
          <b style="color: rgb(var(--warning-6))">有 {{ stats.shaky }} 条比值偏离超过 1.2 倍，值得看一眼。</b>
        </template>
      </div>

      <a-alert v-if="scanResult" type="info" :show-icon="true" style="margin-top: 10px" :title="scanResult" />

      <!-- 没能确定目标的逐条摊开。它们既不登记、也不会被"直接用"——
           也就是说这笔跟单要人工定夺，所以"为什么"只能在这里交代 -->
      <div v-if="scanFailures.length > 0" class="scan-failures">
        <div class="failures-title">
          这些源标的<b>没能自动确定目标</b>（不登记，也不会同名直接用；要跟就人工登记一条）：
        </div>
        <div v-for="failure in scanFailures" :key="failure.sourceBase" class="failure-row">
          <span class="mono">{{ failure.sourceBase }}/{{ failure.sourceQuote }}</span>
          <span class="failure-reason">{{ failure.reason }}</span>
        </div>
      </div>
    </a-card>

    <a-card :bordered="false">
      <a-table
        :data="mappings"
        row-key="id"
        size="small"
        :bordered="false"
        :loading="loading"
        :pagination="{ pageSize: 20, size: 'small' }"
      >
        <template #columns>
          <!-- 交易所列：映射按平台分开存，一行的目标合约属于哪一家必须一眼可见 -->
          <a-table-column title="交易所" :width="170">
            <template #cell="{ record }">
              <a-space :size="6">
                <span class="mono">{{ record.targetPlatform }}</span>
                <a-tag size="small" color="arcoblue">{{ platformLabel(record.targetPlatform) }}</a-tag>
              </a-space>
            </template>
          </a-table-column>

          <!-- 标的是"资产币 / 交易币"两个词，中间不该换行——换行会把它切碎得认不出来 -->
          <a-table-column title="源标的" :width="180">
            <template #cell="{ record }">
              <span class="pair">
                <span class="pair-base">{{ record.sourceBase }}</span>
                <span class="pair-slash">/</span>
                <span class="pair-quote">{{ record.sourceQuote }}</span>
              </span>
            </template>
          </a-table-column>

          <a-table-column title="目标标的" :width="250">
            <template #cell="{ record }">
              <a-space :size="8">
                <!-- 这一格只放**本系统的标的**（资产币/交易币）。
                     平台自己的合约代码（BTCUSDT、BTC-USDT-SWAP）属于对接实现的细节，
                     不下沉到这张表上：它是"喂给某一家"的写法，不是标的的身份 -->
                <span class="pair">
                  <span class="pair-base">{{ record.targetBase }}</span>
                  <span class="pair-slash">/</span>
                  <span class="pair-quote">{{ record.targetQuote }}</span>
                </span>
                <a-tag size="small" :color="record.targetIsTradFi ? 'purple' : 'gray'">
                  {{ record.targetIsTradFi ? '传统金融' : '加密' }}
                </a-tag>
              </a-space>
            </template>
          </a-table-column>

          <a-table-column :width="150" align="right">
            <template #title>
              <HeaderHint
                label="价格比值"
                hint="两侧价格相差的倍数。接近 1 说明两边确实是同一个资产；偏离越大越可疑，超过 3 倍就不该映射。它是判断这条映射还可不可信的唯一量化依据。"
              />
            </template>
            <template #cell="{ record }">
              <span v-if="record.priceRatio === null" style="color: var(--color-text-3)">未验证</span>
              <a-tooltip
                v-else
                :content="`源 ${formatNumber(record.sourcePrice ?? 0, 6)} · 目标 ${formatNumber(record.targetPrice ?? 0, 6)}`"
              >
                <span :style="{ color: ratioTone(record.priceRatio) }">
                  {{ formatNumber(record.priceRatio, 4) }}
                </span>
              </a-tooltip>
            </template>
          </a-table-column>

          <a-table-column title="来源" :width="110" align="center">
            <template #cell="{ record }">
              <a-tag size="small" :color="record.origin === 'manual' ? 'orange' : 'gray'">
                {{ record.origin === 'manual' ? '人工登记' : '扫描' }}
              </a-tag>
            </template>
          </a-table-column>

          <a-table-column title="状态" :width="90" align="center">
            <template #cell="{ record }">
              <a-tag size="small" :color="record.isEnabled ? 'green' : 'gray'">
                {{ record.isEnabled ? '启用' : '停用' }}
              </a-tag>
            </template>
          </a-table-column>

          <a-table-column title="最近验证" :width="110" align="right">
            <template #cell="{ record }">
              <span v-if="record.verifiedAt">{{ formatSince(record.verifiedAt) }}</span>
              <span v-else style="color: var(--color-text-3)">未验证</span>
            </template>
          </a-table-column>

          <a-table-column title="备注" ellipsis tooltip>
            <template #cell="{ record }">
              <span :style="{ color: record.note?.includes('疑似') ? 'rgb(var(--warning-6))' : undefined }">
                {{ record.note ?? '—' }}
              </span>
            </template>
          </a-table-column>

          <a-table-column title="操作" :width="160" align="center">
            <template #cell="{ record }">
              <a-space :size="10">
                <a-link @click="openForm(record)">修正</a-link>
                <a-link @click="toggle(record)">{{ record.isEnabled ? '停用' : '启用' }}</a-link>
                <a-link status="danger" @click="requestDelete(record)">删除</a-link>
              </a-space>
            </template>
          </a-table-column>
        </template>

        <template #empty>
          <a-empty
            :description="
              isAllPlatforms || selectedPlatform === ''
                ? '先在上面的选择器里选一个平台'
                : '还没有映射。点「扫描被观察的池」自动整理一份。'
            "
          />
        </template>
      </a-table>
    </a-card>

    <a-modal
      v-model:visible="formOpen"
      :title="editingId === null ? '登记映射' : '修正映射'"
      :width="560"
      :footer="false"
    >
      <a-space direction="vertical" fill :size="14">
        <a-alert v-if="formError" type="error" :title="formError" />

        <!-- 顺序按用户的心智走：先定交易所（目标平台），再选源与目标两个标的 -->
        <div>
          <div class="field-label">交易所</div>
          <a-select
            v-model="formPlatform"
            :style="{ width: '100%' }"
            :disabled="editingId !== null"
            @change="loadFormOptions"
          >
            <a-option
              v-for="p in platforms"
              :key="p.platform"
              :value="p.platform"
              :label="`${platformLabel(p.platform)}（${p.platform}）`"
              :disabled="!p.supported"
            >
              {{ platformLabel(p.platform) }}
              <span class="mono" style="color: var(--color-text-3)">{{ p.platform }}</span>
              <span v-if="!p.supported"> · 暂无合约目录</span>
            </a-option>
          </a-select>
          <div class="field-hint">映射的目标交易所。目标合约清单按它加载（各家合约不同）。</div>
        </div>

        <div>
          <div class="field-label">源标的</div>
          <div class="pair-fields">
            <a-select
              v-model="sourceBase"
              class="pair-fields-base"
              placeholder="Lighter 标的（可搜）"
              allow-search
              :loading="sourceLoading"
              :disabled="editingId !== null"
              :filter-option="filterSymbol"
            >
              <a-option
                v-for="s in sourceOptions"
                :key="s.symbol"
                :value="s.symbol"
                :label="s.symbol"
              >
                {{ s.symbol }}
              </a-option>
            </a-select>
            <a-input
              :model-value="SourceQuote"
              disabled
              class="pair-fields-quote"
              :style="{ flex: '0 0 132px' }"
            />
          </div>
          <div class="field-hint">
            源侧是 Lighter 的市场：资产币从 Lighter 的全部标的里选，交易币固定
            <span class="mono">USDC</span>（实测它的逐市场交易币字段恒为 0，只能按系统口径取）。
          </div>
        </div>

        <div>
          <div class="field-label">目标标的</div>
          <div class="pair-fields">
            <a-select
              v-model="targetBase"
              class="pair-fields-base"
              placeholder="资产币（可搜）"
              allow-search
              :loading="symbolsLoading"
              :filter-option="filterSymbol"
            >
              <a-option
                v-for="s in baseAssets"
                :key="s.baseAsset"
                :value="s.baseAsset"
                :label="s.baseAsset"
              >
                {{ s.baseAsset }}
                <span v-if="s.isTradFi">· 传统金融</span>
                · 最小名义价值 {{ s.minNotional }}
              </a-option>
            </a-select>

            <!-- 交易币按所选资产币**级联**：它取决于交易所给这个资产币开了哪些报价币 -->
            <!-- 只有该交易所给这个资产币开了多个报价币时才可下拉。实测币安有 39 个资产币
                 是多报价的（SPCXUSDT 与 SPCXUSD1 就是一例），所以这里常常是真的有得选。
                 一个选项时显示成只读——用点不动的下拉伪装成"可选项"更费解
                 （使用者实测反馈"无法选择交易币"，根因就在这）。
                 宽度用内联样式：Arco 的 select 自带 flex，同优先级的类选择器会被它盖过。 -->
            <a-select
              v-if="targetQuotes.length > 1"
              v-model="targetQuote"
              class="pair-fields-quote"
              :style="{ flex: '0 0 132px' }"
              placeholder="交易币"
            >
              <a-option v-for="q in targetQuotes" :key="q" :value="q" :label="q">{{ q }}</a-option>
            </a-select>
            <a-input
              v-else
              :model-value="targetQuote || '—'"
              disabled
              class="pair-fields-quote"
              :style="{ flex: '0 0 132px' }"
            />
          </div>
          <div class="field-hint">
            两段都从该交易所的合约清单里选：手输一个不存在的组合，失败会推迟到真下单那一刻——那时是拿真钱在试。
            交易币只有在该交易所给这个资产币开了多个报价币时才可选；只有一种时它就是事实上的固定值。
            <template v-if="targetBase !== ''">
              已选：<span class="mono">{{ targetBase }}/{{ targetQuote || '…' }}</span>
            </template>
          </div>
        </div>

        <a-checkbox v-model="isEnabled">启用（停用后跟单路径视为未映射，但记录仍在）</a-checkbox>

        <div>
          <div class="field-label">备注</div>
          <a-input v-model="note" placeholder="为什么这么映射（例如：WTI 在币安叫 CL）" />
        </div>

        <a-alert v-if="editingId !== null" type="info" :show-icon="true">
          人工修正后这条记录会标记为「人工登记」，<b>扫描不再覆盖它</b>；
          同时上一次的价格验证结果会被清空——人工改过目标标的，旧结论就不再适用。
        </a-alert>

        <div class="modal-actions">
          <a-space>
            <a-button @click="formOpen = false">取消</a-button>
            <a-button type="primary" :loading="saving" @click="submit">确定</a-button>
          </a-space>
        </div>
      </a-space>
    </a-modal>

    <a-modal
      v-model:visible="deleteOpen"
      title="删除这条映射？"
      ok-text="删除"
      cancel-text="取消"
      @ok="confirmDelete"
    >
      <div style="font-size: 13px; line-height: 1.8">
        将删除 <span class="mono">{{ deleteTarget?.sourcePair }}</span> →
        <span class="mono">{{ deleteTarget?.targetPair }}</span>。
        跟单路径随后会把该标的记成"映射表里没有"，而不是继续用这条。
      </div>
    </a-modal>
  </a-space>
</template>

<style scoped>
.scan-failures {
  margin-top: 10px;
  padding: 10px 12px;
  background: var(--color-fill-1);
  border-radius: 4px;
  font-size: 12px;
  line-height: 1.8;
}

.failures-title {
  color: var(--color-text-2);
  font-weight: 500;
}

.failure-row {
  display: flex;
  gap: 10px;
  align-items: baseline;
}

.failure-reason {
  color: var(--color-text-3);
}


/* 合约代码、标的这类"机器写的字"一律等宽：变量名与数字在比例字体里对不齐，读起来得多看两遍 */
.mono {
  font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
}

.hint-line {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.8;
}


/* 标的：两个词各自不折行（列窄时宁可溢出，也不要出现 ANTHROPIC 被切成 ANT/HRO/PIC） */
.pair {
  display: inline-flex;
  align-items: baseline;
  gap: 2px;
  white-space: nowrap;
  font-family: monospace;
}

.pair-base {
  font-weight: 500;
}

.pair-slash {
  color: var(--color-text-4);
}

.pair-quote {
  color: var(--color-text-3);
}

.field-label {
  font-size: 13px;
  margin-bottom: 6px;
}

/* 标的的两个字段并排：资产币占大头（名字长），交易币只占一小截 */
.pair-fields {
  display: flex;
  gap: 8px;
}

.pair-fields-base {
  flex: 1 1 auto;
  min-width: 0;
}

/* 只用来说明这一侧是"交易币那一段"，宽度由元素上的内联 flex 决定（见上面的理由） */
.pair-fields-quote {
  flex-shrink: 0;
}

/* 弹窗底部的按钮右对齐：与项目里其它弹窗（交易所、带单员）保持同一位置，
   使用者的手会记住它 */
.modal-actions {
  display: flex;
  justify-content: flex-end;
}

.field-hint {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.7;
  margin-top: 6px;
}
</style>
