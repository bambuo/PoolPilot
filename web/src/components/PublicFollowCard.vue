<script setup lang="ts">
import type { PublicFollowerSummary } from '@/api/public'
import { formatDate, formatLocalDateTime, formatNumber, formatPercent, formatUsd } from '@/utils/format'

/// 公开跟单页的一张留言板卡片（G2）。
///
/// 形制照工作台的跟单卡（`FollowListView` 的 `.account-card`）：标题 + 标签一行、
/// 收益率当锚点、`跟单员 #id · 本金 · 比例` 一行、**固定两行高**的持仓视口、卡片脚一排计数。
/// 那几条排版纪律是工作台先踩出来的，这里照抄，不另立一套：
///   · 持仓区高度不随标的数变——否则一排卡片参差不齐，卡片脚对不齐同一条水平线；
///     超出两行由轨道自动上下滚动，`prefers-reduced-motion` 下退回手动滚动；
///   · 标签浅（text-3）、数值深（text-1）且等宽；
///   · 标题只放名字并省略号截断，标签组不换行；"上游已暂停"是一句警示而不是标签。
///
/// 三条本仓口径在这里落实，改动前先读：
///   · `isValuationAvailable === false` 时显示"估值不可得"，**不能**把 equity/盈亏当 0 渲染；
///   · `skipCount > 0` 显著标出——那是"带单员动了但我没跟上"，不是中性的计数；
///   · 删除入口**只在 `isOwnedByCaller` 时渲染**（服务端按 Cookie 身份判定，前端不自己猜）。
///
/// 公开契约里没有 exchangeName / exchangeConfigId / availableBalance / livePositions
/// 这些实盘字段——这里不显示，也不许想办法显示。
///
/// 与工作台的唯一结构差别：那张卡是 `RouterLink`（点卡进详情页），而公开侧不注册详情路由
/// （详情是弹窗）。所以这里给整张卡挂点击 + 键盘可达（Enter），动作只留一个「删除」——
/// 把「详情」也做成按钮会挤进卡片脚，320px 宽的卡上三个计数加两个链接必然折行。
defineProps<{
  follower: PublicFollowerSummary
}>()

// 事件都不带参数：父层拿着这张卡自己的 id 就够（详情按 id 拉 G3，删除按 id 发 W3）
const emit = defineEmits<{
  detail: []
  remove: []
}>()

/// 持仓视口的可见行数。样式里 `--position-viewport` 的"两行"就是它，两处共用一个数
const POSITION_ROWS_VISIBLE = 2
</script>

<template>
  <article
    class="account-card"
    tabindex="0"
    title="点击查看这条跟单的详情"
    @click="emit('detail')"
    @keyup.enter="emit('detail')"
  >
    <div class="card-head">
      <span class="card-title">{{ follower.label || follower.leadName }}</span>
      <a-space :size="4">
        <a-tag v-if="follower.isOwnedByCaller" size="small" color="arcoblue">你创建的</a-tag>
        <a-tag size="small" :color="follower.isActive ? 'green' : 'gray'">
          {{ follower.isActive ? '跟随中' : '已暂停' }}
        </a-tag>
      </a-space>
    </div>

    <!-- 估值可得：头条是收益率 + 权益。不可得：整段数字都不渲染——
         一句"估值不可得"和一堆按 0 画的图，前者才诚实（口径与工作台一致） -->
    <template v-if="follower.isValuationAvailable">
      <div class="card-equity">
        <span
          class="equity-value"
          :style="{
            color: follower.returnPercent >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
          }"
        >
          {{ formatPercent(follower.returnPercent, 3) }}
        </span>
        <!-- 净值是**金额**，不是说明文字：给它数值档（深一档） -->
        <span class="equity-abs is-value">{{ formatUsd(follower.equity) }}</span>
      </div>

      <div class="card-meta">
        跟单员 #{{ follower.id }} · 本金
        <span class="meta-value">{{ formatUsd(follower.initialCapital) }}</span>
        · 比例
        <span class="meta-value">{{ formatPercent(follower.scale * 100, 3) }}</span>
      </div>

      <div
        class="card-positions"
        :class="{ 'is-scrolling': follower.positions.length > POSITION_ROWS_VISIBLE }"
      >
        <div class="positions-track">
          <template v-if="follower.positions.length > 0">
            <div v-for="position in follower.positions" :key="position.symbol" class="position-row">
              <span>
                <a-tag size="small" :color="position.side === 'long' ? 'green' : 'red'">
                  {{ position.side === 'long' ? '多' : '空' }}
                </a-tag>
                {{ position.symbol }} {{ formatNumber(position.size, 1) }}
              </span>
              <span
                :style="{
                  color:
                    position.unrealizedPnl >= 0 ? 'rgb(var(--success-6))' : 'rgb(var(--danger-6))',
                }"
              >
                <!-- 卡片现在也逐行情帧更新；2 位小数会把大多数跳动舍成同一个数，留 3 位 -->
                {{ formatUsd(position.unrealizedPnl, 3) }}
              </span>
            </div>
          </template>
          <span v-else class="card-idle">当前空仓</span>
        </div>
      </div>

      <div class="card-foot">
        <span>成交 {{ follower.fillCount }}</span>
        <span :style="{ color: follower.skipCount > 0 ? 'rgb(var(--warning-6))' : undefined }">
          未跟上 {{ follower.skipCount }}
        </span>
        <span>名义价值 <span class="meta-value">{{ formatUsd(follower.grossNotional) }}</span></span>
        <!-- 删除入口只跟 isOwnedByCaller 走：服务端按 Cookie 身份判定"是谁创建的"，
             前端不复制这条规则，也拿不到（公开 DTO 里根本没有创建者身份的其他字段）。
             @click.stop：点删除不该顺手把详情也打开 -->
        <a-link
          v-if="follower.isOwnedByCaller"
          class="foot-actions"
          status="danger"
          @click.stop="$emit('remove')"
        >
          删除
        </a-link>
      </div>

      <div v-if="!follower.leadIsEnabled" class="card-warn">
        订阅的带单员已暂停，不会再有新动作
      </div>
    </template>

    <template v-else>
      <div class="card-meta">
        跟单员 #{{ follower.id }} · 订阅 {{ follower.leadName }} · 本金
        <span class="meta-value">{{ formatUsd(follower.initialCapital) }}</span>
      </div>
      <div class="card-warn">
        暂取不到池子数据，估值不可得（收益率与盈亏此刻没有意义，这条跟单本身仍在跑）
      </div>
      <div v-if="follower.isOwnedByCaller" class="card-foot">
        <a-link class="foot-actions" status="danger" @click.stop="$emit('remove')">删除</a-link>
      </div>
    </template>

    <!-- 创建时刻与带单员卡片的 #索引 戳记同一档：排查用，不参与日常阅读，悬停才给全 -->
    <a-tooltip :content="`成立于 ${formatLocalDateTime(follower.createdAt)}`">
      <span class="card-stamp">{{ formatDate(follower.createdAt) }}</span>
    </a-tooltip>
  </article>
</template>

<style scoped>
/* 以下取值与工作台的 .account-card 同一份（尺寸、边框、锚点字号），不另立一套 */
.account-card {
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

/* 整张卡可点（详情），悬停与键盘聚焦都给同样的提示——工作台的卡是 RouterLink，
   靠的就是这两条；这里没有链接可用，就自己把"可点"这件事说出来 */
.account-card:hover,
.account-card:focus-visible {
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
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-equity {
  display: flex;
  align-items: baseline;
  gap: 10px;
}

/* 大字：收益率（内联覆盖成红绿）。显式给最深一档而不是继承——它是卡片的视觉锚点。
   等宽数字：一排卡片横扫过去时，位数不同的金额也能对齐 */
.equity-value {
  font-size: 24px;
  font-weight: 500;
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

.equity-abs {
  font-size: 13px;
  color: var(--color-text-3);
}

/* 这一格放的是**净值金额**（不是说明文字）：与收益率同属"要看的数"，给它数值档 */
.equity-abs.is-value {
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

/* 行内的金额与比例：标签浅、数值深（工作台同一条规则） */
.meta-value {
  color: var(--color-text-1);
  font-variant-numeric: tabular-nums;
}

.card-meta,
.card-foot {
  font-size: 12px;
  color: var(--color-text-3);
}

/* 持仓区 = 固定高度的**视口**，高度按"两行"算出来而不是写死像素：
   行高与行距是变量，改一处两边都跟着变（视口写死、行高改了就会出现半行） */
.card-positions {
  --position-row-height: 24px;
  --position-row-gap: 4px;
  --position-viewport: calc((var(--position-row-height) * 2) + var(--position-row-gap));

  height: var(--position-viewport);

  /* 超出部分由轨道带出来，不给原生滚动条——列表里十几张卡片各挂一条滚动条太吵。
     但 prefers-reduced-motion 下会退回可滚动（见文件末尾），保证第三行之后够得着 */
  overflow: hidden;
  font-size: 13px;
  padding: 8px 0;
  border-top: 1px solid var(--color-border-1);
  border-bottom: 1px solid var(--color-border-1);
}

/* 会动的只有这一条轨道：视口（边框与内边距）不动，动的是里面的行 */
.positions-track {
  display: flex;
  flex-direction: column;
  gap: var(--position-row-gap);
}

.position-row {
  display: flex;
  align-items: center;
  justify-content: space-between;

  /* 行高固定：否则"两行的高度"这个算法不成立，标签换行还会把视口撑破 */
  height: var(--position-row-height);
}

/* 超过两行：自动上下滚动（跑马灯）。用 alternate 来回走，而不是单向循环——
   单向循环要复制一份内容才能无缝，而这里只有几行，"来回翻看"也更贴近它表达的东西。
   悬停暂停：鼠标压在上面时不该继续动 */
.card-positions.is-scrolling .positions-track {
  animation: positions-pingpong 4s ease-in-out infinite alternate;
}

.card-positions.is-scrolling:hover .positions-track {
  animation-play-state: paused;
}

@keyframes positions-pingpong {
  from {
    transform: translateY(0);
  }

  /* 百分比相对**轨道自身**高度：-100% 是滚到底，再加回一个视口高度，
     正好让最后一行停在视口底部（多滚一点就会露出空白） */
  to {
    transform: translateY(calc(-100% + var(--position-viewport)));
  }
}

/* 声明"减少动效"的用户：不做动画，改成可手动滚动 */
@media (prefers-reduced-motion: reduce) {
  .card-positions.is-scrolling {
    overflow-y: auto;
  }

  .card-positions.is-scrolling .positions-track {
    animation: none;
  }
}

.card-idle {
  color: var(--color-text-3);
}

.card-foot {
  display: flex;
  align-items: center;
  gap: 14px;

  /* 320px 的卡上三个计数已经排满：不换行的前提是每格自己也不折。
     之前"成交 6"被拆成两行、"详情/删除"竖排，就是少了这两条 */
  flex-wrap: nowrap;
  white-space: nowrap;
}

/* 删除跟在计数之后靠右（a-link，不是按钮——工作台的动作一律是链接） */
.foot-actions {
  margin-left: auto;
}

.card-warn {
  font-size: 12px;
  color: rgb(var(--warning-6));
}

/* 与带单员卡片的 #索引 戳记同一档：排查用，不该参与日常阅读 */
.card-stamp {
  position: absolute;
  right: 14px;
  bottom: 4px;
  font-size: 10px;
  color: var(--color-text-4);
  cursor: help;
}
</style>
