<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'

import { api, type PoolSummary } from '@/api/client'
import HeaderHint from '@/components/HeaderHint.vue'
import { formatAgeSince, formatCompact, formatNumber, formatPercent, formatUsd } from '@/utils/format'

/// 池列表。
///
/// 这一页只展示**一次请求就能拿到全部池**的廉价字段。真实回撤 / 真实 Sharpe 需要
/// 逐池拉 150 天净值曲线，放在详情页按需加载——列表页做 N 次网络请求是不可接受的。
///
/// 池名与「详情」都用 RouterLink 而不是 Arco 的 `a-link`：后者不生成 `href`，
/// 渲染出来的 `<a>` 既不能被键盘聚焦、也不能用新标签页打开，对辅助技术也不可见。
/// 真实链接让「进入详情」这件事回到浏览器本来的语义上。
const pools = ref<PoolSummary[]>([])
const loading = ref(false)
const errorMessage = ref('')
const filter = ref<'user' | 'protocol' | 'stake' | 'all'>('user')

/** 池名（未命名时兜底）。列表与 title 共用，避免两处口径不一。 */
function poolTitle(record: PoolSummary): string {
  return record.name || '（未命名）'
}

/** 详情页路由。集中一处，避免两个入口的路径写法不一致。 */
function detailRoute(record: PoolSummary) {
  return { name: 'pool-detail' as const, params: { accountIndex: String(record.accountIndex) } }
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = ''

  try {
    pools.value = await api.listPools(filter.value)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <a-space direction="vertical" fill style="width: 100%">
    <a-alert type="warning" :show-icon="true">
      <template #title>两个口径必须先分清</template>
      <div style="font-size: 13px; line-height: 1.8">
        <div>
          <b>年化利率</b>与<b>夏普比率</b>是上游直接给出的点估计（表头 ? 可看含义）。
          实测年化利率的取值可以到数千万个百分点、也会出现负数，
          所以本页把它们标灰，只用于横向粗筛，不作为收益预期。
        </div>
        <div>
          <b>真实回撤 / 实算夏普比率</b>由本机从净值曲线实算，上游完全不提供。逐池拉曲线成本高，放在详情页。
        </div>
      </div>
    </a-alert>

    <a-card :bordered="false">
      <template #title>
        <a-space>
          <span>公共资金池</span>
          <a-tag v-if="!loading" size="small">{{ pools.length }} 个</a-tag>
        </a-space>
      </template>

      <template #extra>
        <a-space>
          <a-radio-group v-model="filter" type="button" size="small" @change="load">
            <a-radio value="user">用户资金池</a-radio>
            <a-radio value="protocol">协议池</a-radio>
            <a-radio value="stake">质押池</a-radio>
            <a-radio value="all">全部</a-radio>
          </a-radio-group>
          <a-button size="small" :loading="loading" @click="load">刷新</a-button>
        </a-space>
      </template>

      <a-alert v-if="errorMessage" type="error" :title="errorMessage" style="margin-bottom: 12px" />

      <a-table
        :data="pools"
        :loading="loading"
        row-key="accountIndex"
        :pagination="{ pageSize: 20, showTotal: true, size: 'small' }"
        :bordered="false"
        size="small"
        :scroll="{ x: 1320 }"
      >
        <template #columns>
          <!--
            池名称加宽到 260 并截断：实测最长的名字（"Algorithmic L/S | Big Brain Research"）
            超过旧宽度会把相邻列挤变形。省略号直接写在链接样式上——
            Arco 的 column ellipsis 对自定义插槽内容不生效（实测样式没应用上）。
            全名出口用原生 title：截断必须有全名出口，否则使用者无从分辨两个长名字。
          -->
          <a-table-column title="池名称" data-index="name" :width="260" fixed="left">
            <template #cell="{ record }">
              <RouterLink
                class="cell-link name-link"
                :to="detailRoute(record as PoolSummary)"
                :title="poolTitle(record as PoolSummary)"
              >
                {{ poolTitle(record as PoolSummary) }}
              </RouterLink>
            </template>
          </a-table-column>

          <!-- 运营方归属：判定与官方前端同源（见后端 Pool.Origin）。
               社区池占多数，官方池用醒目色以突出——杠杆代币线属于官方产品，
               和第三方策略池的风险画像完全不同。 -->
          <a-table-column title="运营方" :width="100" align="center">
            <template #title>
              <HeaderHint
                label="运营方"
                hint="LIGHTER = Lighter 官方运营（含官方杠杆代币池线与 XLP）；社区 = 第三方运营者。判定口径与 Lighter 官方前端一致。"
              />
            </template>
            <template #cell="{ record }">
              <a-tooltip :content="`运营者钱包 ${record.operatorAddress}`">
                <a-tag size="small" :color="record.origin === 'lighter' ? 'arcoblue' : 'gray'">
                  {{ record.origin === 'lighter' ? 'LIGHTER' : '社区' }}
                </a-tag>
              </a-tooltip>
            </template>
          </a-table-column>

          <!-- 状态列：冻结 = 停止接受新入金（既有仓位与净值继续）。
               实测约 43% 的池处于冻结态，所以<b>不做服务端过滤</b>——藏起来会让池子凭空消失，
               而使用者常需要看到它们（例如判断某个运营者是否已经收摊）。 -->
          <a-table-column title="状态" :width="95" align="center">
            <template #title>
              <HeaderHint
                label="状态"
                hint="正常 = 接受入金；已冻结 = 停止接受新入金（既有仓位与净值继续）。冻结通常意味着运营者已收摊，「直接入池」选项随之不存在，但跟单不受影响。"
              />
            </template>
            <template #cell="{ record }">
              <a-tag size="small" :color="record.isFrozen ? 'gray' : 'green'">
                {{ record.isFrozen ? '已冻结' : '正常' }}
              </a-tag>
            </template>
          </a-table-column>

          <!-- 账户索引列已隐藏：它是机器标识（详情页与链接里仍可见），
               在列表里占一整列只会稀释真正要看的数字 -->
          <a-table-column title="总资产" :width="135" align="right">
            <template #title>
              <HeaderHint
                label="总资产"
                hint="池内所有资产按当前价格折算的总价值，随行情与申赎实时变动。"
              />
            </template>
            <template #cell="{ record }">{{ formatUsd(record.totalAssetValue) }}</template>
          </a-table-column>

          <a-table-column title="年化利率" :width="150" align="right">
            <template #title>
              <HeaderHint
                label="年化利率"
                hint="上游直接上报的年化收益率点估计。实测取值可到数千万个百分点、也会出现负数，只能作横向粗筛，不可作为收益预期；更可信的实算年化见池详情页。"
              />
            </template>
            <template #cell="{ record }">
              <!-- 正绿负红。红绿只表达方向，不表达可信度——"这是不可信的点估计"由表头 ? 负责 -->
              <span
                :style="{
                  color:
                    record.reportedApyPercent >= 0
                      ? 'rgb(var(--success-6))'
                      : 'rgb(var(--danger-6))',
                }"
              >
                {{ formatPercent(record.reportedApyPercent) }}
              </span>
            </template>
          </a-table-column>

          <a-table-column title="夏普比率" :width="135" align="right">
            <template #title>
              <HeaderHint
                label="夏普比率"
                hint="每承担一单位波动换得的收益，越高越好。此为上游上报的点估计；本机按净值序列的实算值在池详情页并列展示。"
              />
            </template>
            <template #cell="{ record }">
              <!-- 只有负值标红：夏普为正是"称职"，用标准色；为负是"跑输无风险"，才值得警示 -->
              <span
                :style="{
                  color: record.reportedSharpe < 0 ? 'rgb(var(--danger-6))' : undefined,
                }"
              >
                {{ formatNumber(record.reportedSharpe, 2) }}
              </span>
            </template>
          </a-table-column>

          <a-table-column title="运营费" :width="105" align="right">
            <template #title>
              <HeaderHint
                label="运营费"
                hint="运营者从池子收益中抽取的分成比例，直接入池时按此扣减；跟单不经过运营者，但承担执行摩擦（见跟单页的成本测量）。"
              />
            </template>
            <template #cell="{ record }">{{ formatPercent(record.operatorFeePercent) }}</template>
          </a-table-column>

          <a-table-column title="份额" :width="130" align="right">
            <template #title>
              <HeaderHint
                label="份额"
                hint="池子发行的总份额数量。份额净值 = 总资产 ÷ 份额，是净值曲线的基本单位。"
              />
            </template>
            <template #cell="{ record }">{{ formatCompact(record.totalShares) }}</template>
          </a-table-column>

          <a-table-column title="运营时长" :width="120">
            <template #cell="{ record }">{{ formatAgeSince(record.createdAt) }}</template>
          </a-table-column>

          <a-table-column title="操作" :width="90" fixed="right" align="center">
            <template #cell="{ record }">
              <RouterLink class="cell-link" :to="detailRoute(record as PoolSummary)">详情</RouterLink>
            </template>
          </a-table-column>
        </template>

        <template #empty>
          <a-empty description="没有符合条件的池" />
        </template>
      </a-table>
    </a-card>
  </a-space>
</template>

<style scoped>
/* 表格里的链接。RouterLink 渲染的是真 `<a href>`，所以这里只负责观感：
   去掉下划线、给一个可辨识的颜色和指针，并与 Arco 的链接色一致。 */
.cell-link {
  color: rgb(var(--primary-6));
  text-decoration: none;
  cursor: pointer;
}

.cell-link:hover {
  color: rgb(var(--primary-5));
  text-decoration: underline;
}

/* 池名称列的截断。写在链接本身而不是指望 Arco 的 column ellipsis——
   它对自定义插槽内容不生效（实测样式没应用上）。 */
.name-link {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
