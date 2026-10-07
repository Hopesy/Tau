// 作者：xxx
/**
 * 【CodingAgent】【扩展验收】注册使用会话元数据计数的合成扩展
 * @param {object} pi 扩展 API
 * @returns {void} 无返回值
 */
module.exports = function registerSessionMetadata(pi) {
  pi.on("agent_start", recordSessionTurn);

  /**
   * 【CodingAgent】【扩展验收】从当前分支恢复计数并保存名称和标签，数据不发送给模型
   * @param {object} _event Agent 开始事件
   * @param {object} ctx 当前扩展上下文
   * @returns {void} 无返回值
   */
  function recordSessionTurn(_event, ctx) {
    // 1. 【CodingAgent】【状态恢复】只读取当前分支上属于本扩展的条目
    const entries = ctx.sessionManager.getBranch();
    const previous = entries.filter(entry => entry.type === "custom" && entry.customType === "tau-session-smoke").at(-1);
    const turn = Number(previous?.data?.turn ?? 0) + 1;
    // 2. 【CodingAgent】【状态保存】记录合成标记，供检查请求正文未包含此状态
    pi.appendEntry("tau-session-smoke", { turn, marker: "TAU_EXTENSION_STATE_ONLY" });
    const entryId = ctx.sessionManager.getLeafId();
    pi.setLabel(entryId, "extension-turn-" + turn);
    pi.setSessionName("Extension session " + turn);
  }
};
