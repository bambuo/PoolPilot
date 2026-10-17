<script setup lang="ts">
import { ref, watch } from 'vue'

import { describePublicError, publicApi } from '@/api/public'
import PublicLeadSelect from '@/components/PublicLeadSelect.vue'

/// 公开页的"开一条纸面跟单"表单（W2）。
///
/// 选的是**带单员**而不是池：跟单员订阅的是带单员的信号，带单员与池 1:1（它的身份就是
/// 池索引）。服务端对"池上没有观察者"直接回 400，也不会替你补一个观察者——
/// 建观察者是公开带单页的独立动作（见 PublicLeadSelect 的说明）。
///
/// 与工作台新建跟单的差别在这里是契约性的：**只有纸面**。mode 恒为 'paper'、
/// exchangeConfigId 恒为 null——服务端硬性拒绝 live。前端**不做静默降级**
/// （不传、改成 paper 再来一遍之类的把戏都不许有）：真收到 400 就把 {"error"} 原样显示。
///
/// 校验口径：本金 10–10,000；label 前端 maxlength 24 + 提交前 trim，
/// 超长的兜底交给服务端 400（那句话会原样出现在错误行里）。
/// 布局与工作台「开始跟单」一致：普通字段标签 + 输入框；模式固定为纸面，用一行灰字说明；
/// 本金过小时的影响放在底部 info alert（与工作台同一条），不再用 tooltip。

/// 与服务端区间一致的两个界值写成常量：模板里的 min/max 与提交前的核对共用同一份，
/// 不会出现"输入框放行、提交被拦"的两个口径
const CAPITAL_MIN = 10
const CAPITAL_MAX = 10_000
const LABEL_MAX_LENGTH = 24

const visible = defineModel<boolean>('visible', { required: true })

const emit = defineEmits<{
  /// 成功只回传新条目的 id：父层用它确认推送是否已经把这条带到列表里
  /// （服务端写成功会立即推一帧），不再回传整个对象当第二份列表状态。
  created: [id: number]
}>()

const newLeadIndex = ref('')

/// 本金：绑定类型写死为 number——Arco 的 InputNumber 在 exactOptionalPropertyTypes
/// 下不接受 `number | undefined` 的绑定（清空在类型上是"值没了"，prop 声明里并没有
/// undefined 这一档；这坑本仓踩过，绑定口径与工作台两个页面一致）。
/// 清空在**运行时**给到什么由提交前的 typeof 收窄兜住，不让 `>= 10` 去猜 null/NaN 的边界。
const newCapital = ref<number>(100)

const newLabel = ref('')
const submitting = ref(false)
const errorMessage = ref('')

async function submit(): Promise<void> {
  errorMessage.value = ''

  if (newLeadIndex.value === '') {
    errorMessage.value = '请先选择一个要跟随的带单员。'
    return
  }

  const capital = newCapital.value

  if (typeof capital !== 'number' || !Number.isFinite(capital) || capital < CAPITAL_MIN || capital > CAPITAL_MAX) {
    errorMessage.value = `本金要在 ${CAPITAL_MIN} 到 ${CAPITAL_MAX} 之间。`
    return
  }

  const label = newLabel.value.trim()

  submitting.value = true

  try {
    const created = await publicApi.createFollower({
      leadAccountIndex: Number(newLeadIndex.value),
      initialCapital: capital,
      label: label === '' ? null : label,
      mode: 'paper',
      exchangeConfigId: null,
    })

    visible.value = false
    emit('created', created.id)
  } catch (error) {
    errorMessage.value = describePublicError(error)
  } finally {
    submitting.value = false
  }
}

/// 每次真正打开都回到空白表单：a-modal 的内容只创建一次、之后一直活着，
/// 不把上一次的输入清掉，人会在"本金 8888 + 上次的备注"里开始下一次提交。
/// （放在这里而不是让父层隔着 ref 调 reset：状态的清理跟着状态走，职责不外溢。）
watch(visible, (open) => {
  if (!open) return

  newLeadIndex.value = ''
  newCapital.value = 100
  newLabel.value = ''
  errorMessage.value = ''
})
</script>

<template>
  <a-modal v-model:visible="visible" title="开一条纸面跟单">
    <a-space direction="vertical" fill :size="14">
      <a-alert v-if="errorMessage" type="error" :title="errorMessage" />

      <div>
        <div class="field-label">选择要跟随的带单员</div>
        <PublicLeadSelect v-model="newLeadIndex" />
      </div>

      <div>
        <div class="field-label">跟单模式</div>
        <div class="mode-line">纸面 · 按真实盘口模拟成交，不碰任何真实资金。</div>
      </div>

      <div>
        <div class="field-label">投入的本金</div>
        <a-input-number
          v-model="newCapital"
          :min="CAPITAL_MIN"
          :max="CAPITAL_MAX"
          :step="100"
          :style="{ width: '100%' }"
        >
          <template #suffix>USDC</template>
        </a-input-number>
      </div>

      <div>
        <div class="field-label">备注名（可选）</div>
        <a-input
          v-model="newLabel"
          :max-length="LABEL_MAX_LENGTH"
          placeholder="给这条跟单起个名"
          :style="{ width: '100%' }"
        />
      </div>

      <a-alert type="info" :show-icon="true">
        本金太小时，等比仓位会低于市场的最小下单额，账户建不出仓，只会记录一串「未跟上」；先确认这个池与市场需要多少本金再定。
      </a-alert>
    </a-space>

      <template #footer>
        <div class="modal-actions">
          <a-button @click="visible = false">取消</a-button>
          <a-button type="primary" :loading="submitting" @click="submit">创建跟单</a-button>
        </div>
      </template>
    </a-modal>
</template>

<style scoped>
.field-label {
  font-size: 13px;
  margin-bottom: 6px;
}

.mode-line {
  font-size: 12px;
  color: var(--color-text-3);
  line-height: 1.7;
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 2px;
}
</style>
