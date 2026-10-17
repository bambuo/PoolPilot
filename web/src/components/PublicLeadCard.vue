<script setup lang="ts">
import type { PublicLeadTrader } from '@/api/public'
import { formatAgeSince, formatLocalDateTime, formatSince } from '@/utils/format'

/// 公开带单页的一张卡片：**没有暂停/删除入口**——那是管理员在工作台上的事；
/// 匿名使用者在这里只能"看"和"新增"。
///
/// 形制照工作台的带单员卡片（`LeadListView`）：标题 + 状态标签一行、仓位变动次数当锚点、
/// 最近变动与跟踪时长一行、跟随者数一行、账户索引缩成右下角的灰色戳记。
/// 文案按跟单产品的标准叫法（跟随者 / 仓位变动 / 跟踪天数），不把池子运营口径
/// （信号、份额、运营者）带到公开面。
/// 差别的只有两处，都是契约给的字段不同：
/// · 状态只有"观察中/未在观察"两态——公开侧拿不到 `isEnabled`，
///   区分不了"管理员暂停"与"连接断了"，就不替用户猜（工作台的三态标签因此搬不过来）；
/// · 跟随数只有总数，没有"其中几个在跟随"的拆分。
///
/// 整张卡可点开详情（收益率、回撤与前几大持仓），与公开跟单卡同一交互：
/// 卡片本身是按钮语义（Enter 可达），不再多画一个"详情"按钮挤占卡片脚。
defineProps<{ lead: PublicLeadTrader }>()

const emit = defineEmits<{ detail: [] }>()
</script>

<template>
  <article
    class="lead-card"
    tabindex="0"
    title="点击查看这个带单员的详情"
    @click="emit('detail')"
    @keyup.enter="emit('detail')"
  >
    <div class="card-head">
      <span class="card-title">{{ lead.name || `带单员 ${lead.accountIndex}` }}</span>
      <a-tag size="small" :color="lead.isWatching ? 'green' : 'gray'">
        {{ lead.isWatching ? '观察中' : '未在观察' }}
      </a-tag>
    </div>

    <div class="card-signals">
      <a-tooltip content="该带单员被系统记录的仓位变化次数。与有没有人跟随无关——没人跟也会记录。">
        <span class="signals-label">仓位变动</span>
      </a-tooltip>
      <span class="signals-value" :class="{ 'signals-value-empty': lead.signalCount === 0 }">
        {{ lead.signalCount }}
      </span>
      <span class="signals-unit">次</span>
    </div>

    <div class="card-meta">
      <span v-if="lead.lastSignalAt">最近变动 {{ formatSince(lead.lastSignalAt) }}</span>
      <span v-else>还没有仓位变动</span>
      <span class="meta-quiet">已跟踪 {{ formatAgeSince(lead.createdAt) }}</span>
    </div>

    <div class="card-followers">
      <a-tag v-if="lead.followerCount > 0" size="small" color="arcoblue">
        {{ lead.followerCount }} 位跟随者
      </a-tag>
      <span v-else class="card-idle">还没有跟随者</span>
    </div>

    <!-- 池索引与开始跟踪时刻都是排查用的，不参与日常阅读：缩成右下角的戳记，悬停才给全时刻 -->
    <a-tooltip :content="`开始跟踪于 ${formatLocalDateTime(lead.createdAt)}`">
      <span class="card-stamp">#{{ lead.accountIndex }}</span>
    </a-tooltip>
  </article>
</template>

<style scoped>
/* 以下形制与 LeadListView 的带单员卡片同源（尺寸、边框、锚点字号都按那一份来），
   只保留公开侧需要的部分 */
.lead-card {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px 16px 18px;
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-1);
  border-radius: 6px;
  cursor: pointer;
  transition: border-color 0.15s, box-shadow 0.15s;
}

/* 整张卡可点（详情）：悬停与键盘聚焦给同样的提示 */
.lead-card:hover,
.lead-card:focus-visible {
  border-color: rgb(var(--primary-6));
  box-shadow: 0 2px 10px rgb(0 0 0 / 8%);
}

.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.card-title {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-text-1);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-signals {
  display: flex;
  align-items: baseline;
  gap: 6px;
  padding: 8px 0;
  border-top: 1px solid var(--color-border-1);
  border-bottom: 1px solid var(--color-border-1);
}

.signals-label {
  font-size: 12px;
  color: var(--color-text-3);
  cursor: help;
}

/* 卡上主指标：显式给最深一档而不是继承——它是整张卡的视觉锚点。
   等宽数字让一排卡片横扫过去时位数不同的数也能对齐 */
.signals-value {
  margin-left: auto;
  font-size: 22px;
  font-weight: 500;
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

/* 0 条信号不是成就，降到说明档 */
.signals-value-empty {
  color: var(--color-text-3);
}

.signals-unit {
  font-size: 12px;
  color: var(--color-text-3);
}

.card-meta {
  display: flex;
  gap: 12px;
  font-size: 12px;
  color: var(--color-text-3);
}

.meta-quiet {
  color: var(--color-text-4);
}

.card-followers {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 24px;
  font-size: 12px;
  color: var(--color-text-3);
}

.card-idle {
  color: var(--color-text-3);
}

.card-stamp {
  position: absolute;
  right: 14px;
  bottom: 4px;
  font-family: monospace;
  font-size: 10px;
  color: var(--color-text-4);
  cursor: help;
}
</style>
