// 作者：xxx
const fs = require("node:fs");
const path = require("node:path");
const moduleApi = require("node:module");
const readline = require("node:readline");
const { randomUUID } = require("node:crypto");
const { spawn } = require("node:child_process");
const { EventEmitter } = require("node:events");
const { AsyncLocalStorage } = require("node:async_hooks");
const { fileURLToPath, pathToFileURL } = require("node:url");
const resultPrefix = "__TAU_EXTENSION_RESULT__";
const uiRequestPrefix = "__TAU_EXTENSION_UI_REQUEST__";
const uiResponsePrefix = "__TAU_EXTENSION_UI_RESPONSE__";
const uiCancelPrefix = "__TAU_EXTENSION_UI_CANCEL__";
const sessionActionPrefix = "__TAU_EXTENSION_SESSION_ACTION__";
const hostRequestPrefix = "__TAU_EXTENSION_HOST_REQUEST__";
const hostResponsePrefix = "__TAU_EXTENSION_HOST_RESPONSE__";
const hostCancelPrefix = "__TAU_EXTENSION_HOST_CANCEL__";
const waitPrefix = "__TAU_EXTENSION_WAIT__";
const cancelPrefix = "__TAU_EXTENSION_CANCEL__";
const toolUpdatePrefix = "__TAU_EXTENSION_TOOL_UPDATE__";
const backgroundPrefix = "__TAU_EXTENSION_BACKGROUND__";
let livePayload;
let RegistryAssistantMessageStream;
const modelRegistryCallbacks = new Map();
const userBashOperations = new Map();
const activeRequests = new Map();
const extensionEvents = new EventEmitter();
let extensionImportHookInstalled = false;
const requestContext = new AsyncLocalStorage();
const extensions = new Map();
const readyExtensions = new Map();
const extensionProviders = new Map();
const virtualModels = new Map();
const mcpServers = new Map();
const providerVersions = new Map();
const providerPublicationChains = new Map();
let providerVersion = 0;
let uiRequestCounter = 0;
const pendingUiResponses = new Map();
const pendingHostResponses = new Map();

process.stdin.setEncoding("utf8");
const inputLines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
inputLines.on("line", line => {
  if (line.startsWith(cancelPrefix)) {
    const request = JSON.parse(line.slice(cancelPrefix.length));
    activeRequests.get(request.id)?.abort();
    return;
  }
  if (line.startsWith(hostResponsePrefix)) {
    const response = JSON.parse(line.slice(hostResponsePrefix.length));
    const pending = pendingHostResponses.get(response.id);
    if (pending) {
      if (response.progress) {
        try { pending.onUpdate?.(response.value); }
        catch (error) { pending.error ??= error; }
        return;
      }
      pendingHostResponses.delete(response.id);
      pending.resolve(pending.error ? { ok: false, error: formatError(pending.error) } : response);
    }
    return;
  }
  if (line.startsWith(uiResponsePrefix)) {
    handleUiResponse(line.slice(uiResponsePrefix.length));
    return;
  }

  // 1. 【CodingAgent】【扩展协议】并发请求共享扩展实例，但独立收集响应和消息
  try {
    const { id, payload } = JSON.parse(line);
    if (payload.session) livePayload = payload;
    const controller = new AbortController();
    activeRequests.set(id, controller);
    requestContext.run({ id, payload, controller, actions: [] }, () => {
      main(payload).catch(error => write({ ok: false, error: formatError(error), actions: requestContext.getStore().actions }));
    });
  } catch (error) {
    process.stderr.write(formatError(error) + "\n");
  }
});
// 2. 【CodingAgent】【扩展生命周期】宿主退出并关闭输入后释放扩展定时器等资源
inputLines.on("close", () => process.exit(0));

/**
 * 【CodingAgent】【扩展协议】发送带请求标识的响应并保留进程
 * @param {object} result 当前调用的结果
 * @returns {void} 无返回值
 */
function write(result) {
  const { id } = requestContext.getStore();
  if (result.actions) pruneStaleActions(result.actions);
  activeRequests.delete(id);
  process.stdout.write(resultPrefix + JSON.stringify({ id, result }) + "\n");
}

/**
 * 【CodingAgent】【后台动作】活动调用保留返回动作，已结束调用的定时回调直接发送到宿主
 * @param {object} action 完整动作
 * @param {string} filePath 来源扩展
 * @returns {void} 无返回值
 */
function recordAction(action, filePath) {
  const context = requestContext.getStore();
  const payload = currentPayload();
  action._sessionId = payload?.session?.header.id;
  action._extensionGeneration = payload?.extensionGeneration;
  if (context && activeRequests.has(context.id)) context.actions.push(action);
  else {
    const session = readSession();
    process.stdout.write(backgroundPrefix + JSON.stringify({ sessionId: session.header.id, filePath, actions: [action] }) + "\n");
  }
}

/**
 * 【CodingAgent】【替换隔离】移除旧会话积累但尚未交付的消息和界面动作
 * @param {object[]} actions 当前调用积累的动作
 * @returns {void} 原地保留当前实例的动作
 */
function pruneStaleActions(actions) {
  for (let index = actions.length - 1; index >= 0; index--) {
    const action = actions[index];
    if (action._sessionId !== undefined && (action._sessionId !== livePayload?.session?.header.id ||
      action._extensionGeneration !== livePayload?.extensionGeneration)) actions.splice(index, 1);
  }
}

/** 【CodingAgent】【后台快照】@returns {object} 活动请求使用独立快照，后台回调使用最新会话快照 */
function currentPayload() {
  const context = requestContext.getStore();
  return context && activeRequests.has(context.id) ? context.payload : livePayload;
}

/**
 * 【CodingAgent】【扩展交互】请求宿主显示编辑器并关联所属执行请求
 * @param {object} request 编辑器参数
 * @param {AbortSignal|undefined} signal 可选取消信号
 * @returns {Promise<object|undefined>} 用户响应或取消结果
 */
function requestUi(request, signal) {
  if (!request || typeof request !== "object") {
    return Promise.resolve(undefined);
  }
  const runSignal = requestContext.getStore()?.controller.signal;
  signal = signal && runSignal ? AbortSignal.any([signal, runSignal]) : signal ?? runSignal;
  if (signal?.aborted) return Promise.resolve({ cancelled: true });

  const id = "ui-" + (++uiRequestCounter);
  /** 【CodingAgent】【对话取消】@returns {void} 通知宿主取消指定对话 */
  const cancel = () => process.stdout.write(uiCancelPrefix + JSON.stringify({ id }) + "\n");
  return new Promise(resolve => {
    pendingUiResponses.set(id, resolve);
    const context = requestContext.getStore();
    const background = !context || !activeRequests.has(context.id);
    process.stdout.write(uiRequestPrefix + JSON.stringify({ id, requestId: context?.id ?? "", background,
      sessionId: background ? readSession().header.id : undefined, ...request }) + "\n");
    signal?.addEventListener("abort", cancel, { once: true });
  }).finally(() => signal?.removeEventListener("abort", cancel));
}

function handleUiResponse(responseJson) {
  let response;
  try {
    response = JSON.parse(responseJson || "{}");
  } catch {
    return;
  }

  const id = response && typeof response.id === "string" ? response.id : "";
  const resolve = pendingUiResponses.get(id);
  if (!resolve) return;
  pendingUiResponses.delete(id);
  resolve(response);
}

/**
 * 【CodingAgent】【宿主调用】等待宿主操作的真实结果，并刷新当前调用的状态快照
 * @param {string} operation 宿主操作名称
 * @param {object} fields 操作参数
 * @param {boolean} detached 是否独立于发起命令的取消生命周期
 * @param {object} options 可选取消信号和进度回调
 * @returns {Promise<*>} 宿主返回结果
 */
async function requestHost(operation, fields = {}, detached = false, options = {}) {
  const context = requestContext.getStore();
  const session = readSession();
  const id = randomUUID();
  /** 【CodingAgent】【宿主取消】@returns {void} 取消对应宿主操作 */
  const cancel = () => process.stdout.write(hostCancelPrefix + JSON.stringify({ id }) + "\n");
  const response = await new Promise(resolve => {
    pendingHostResponses.set(id, { resolve, onUpdate: update => requestContext.run(context, () => {
      if (update.nestedCallStarted === true) options.onStarted?.();
      else options.onUpdate?.(update);
    }) });
    process.stdout.write(hostRequestPrefix + JSON.stringify({ id, requestId: context?.id ?? "", background: detached || !context || !activeRequests.has(context.id),
      sessionId: session.header.id, aborted: options.signal?.aborted === true, operation, ...fields }) + "\n");
    options.signal?.addEventListener("abort", cancel, { once: true });
  }).finally(() => options.signal?.removeEventListener("abort", cancel));
  if (!response.ok) throw new Error(response.error || "Extension host operation failed.");
  const payload = context && activeRequests.has(context.id) ? context.payload : livePayload;
  if (["newSession", "fork", "switchSession", "reload"].includes(operation) && !response.value?.cancelled)
    return { value: response.value, payload: { ...payload, session: response.session, runtime: response.runtime, extensionGeneration: response.extensionGeneration } };
  if (response.runtime) payload.runtime = response.runtime;
  if (response.session) payload.session = response.session;
  return response.value ?? undefined;
}

/** 【CodingAgent】【目录发布】@param {object} fields 刷新标识及持久化数据 @returns {Promise<boolean>} 宿主是否接受当前代次 */
async function requestProviderPublication(fields) {
  return (await requestProviderService("publishProviderModels", fields)) === true;
}

/** 【CodingAgent】【提供方通道】@param {string} operation 内部操作 @param {object} fields 调用字段 @returns {Promise<*>} 不依赖会话快照的宿主响应 */
async function requestProviderService(operation, fields, signal) {
  const context = requestContext.getStore();
  signal = signal ? AbortSignal.any([signal, context.controller.signal]) : context.controller.signal;
  const id = randomUUID();
  /** 【CodingAgent】【认证取消】@returns {void} 取消对应宿主交互 */
  const cancel = () => process.stdout.write(hostCancelPrefix + JSON.stringify({ id }) + "\n");
  const response = await new Promise(resolve => {
    pendingHostResponses.set(id, { resolve });
    process.stdout.write(hostRequestPrefix + JSON.stringify({ id, requestId: context.id, operation, aborted: signal.aborted, ...fields }) + "\n");
    signal.addEventListener("abort", cancel, { once: true });
  }).finally(() => signal.removeEventListener("abort", cancel));
  if (!response.ok) throw new Error(response.error || "Provider host operation failed.");
  return response.value ?? undefined;
}

/** 【CodingAgent】【同步登录选项】@param {object} request 本次认证请求 @returns {object|undefined} 惰性且同步的设备身份读取选项 */
function createProviderLoginOptions(request) {
  if (!request.deviceIdReplyPath) return undefined;
  const context = requestContext.getStore();
  const sleeper = new Int32Array(new SharedArrayBuffer(4));
  let result;
  return {
    /** 【CodingAgent】【同步安装身份】@returns {string} 宿主安装标识，首次调用才触发持久化 */
    getDeviceId() {
      if (result === undefined) {
        if (!activeRequests.has(context.id) || context.controller.signal.aborted) throw new Error("OAuth login is no longer active.");
        const id = randomUUID();
        // 1. 【CodingAgent】【同步安装身份】同步写入请求，避免阻塞事件循环时 stdout 缓冲区尚未发送
        fs.writeSync(1, hostRequestPrefix + JSON.stringify({ id, requestId: context.id, operation: "providerAuthDeviceId", callId: request.callId }) + "\n");
        const deadline = performance.now() + 10000;
        // 2. 【CodingAgent】【同步安装身份】宿主原子写入独占结果文件，不依赖当前事件循环接收 stdin
        while (!fs.existsSync(request.deviceIdReplyPath)) {
          if (performance.now() >= deadline) throw new Error("OAuth device ID request timed out");
          Atomics.wait(sleeper, 0, 0, 5);
        }
        result = JSON.parse(fs.readFileSync(request.deviceIdReplyPath, "utf8"));
      }
      if (!result.ok) throw new Error(result.error || "OAuth device ID request failed");
      return result.value;
    }
  };
}

/** 【CodingAgent】【运行状态】@returns {object} 已初始化的运行器快照 */
function readRuntime() {
  readSession();
  return currentPayload().runtime;
}

/**
 * 【CodingAgent】【运行控制】按会话操作通道提交同步控制动作
 * @param {string} operation 操作名
 * @param {object} fields 参数
 * @returns {void} 无返回值
 */
function emitRuntimeOperation(operation, fields = {}) {
  const context = requestContext.getStore();
  const session = readSession();
  process.stdout.write(sessionActionPrefix + JSON.stringify({ requestId: context?.id ?? "", background: !context || !activeRequests.has(context.id),
    sessionId: session.header.id, operation, ...fields }) + "\n");
}

function formatError(error) {
  return error && error.message ? String(error.message) : String(error);
}

function isTypeScriptFile(filePath) {
  return typeof filePath === "string" && filePath.toLowerCase().endsWith(".ts");
}

function mergeSchemaOptions(schema, options) {
  const result = { ...schema };
  if (options && typeof options === "object") Object.assign(result, options);
  return result;
}

const typeBoxModuleSource = String.raw`
function mergeSchemaOptions(schema, options) {
  const result = { ...schema };
  if (options && typeof options === "object") Object.assign(result, options);
  return result;
}
function markOptional(schema) {
  const result = { ...(schema || {}) };
  Object.defineProperty(result, "__tauOptional", { value: true, enumerable: false });
  return result;
}
function stripOptional(schema) {
  const result = { ...(schema || {}) };
  return result;
}
function literalType(value) {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return typeof value;
}
export const Type = {
  Any(options = {}) { return mergeSchemaOptions({}, options); },
  Unknown(options = {}) { return mergeSchemaOptions({}, options); },
  Null(options = {}) { return mergeSchemaOptions({ type: "null" }, options); },
  String(options = {}) { return mergeSchemaOptions({ type: "string" }, options); },
  Number(options = {}) { return mergeSchemaOptions({ type: "number" }, options); },
  Integer(options = {}) { return mergeSchemaOptions({ type: "integer" }, options); },
  Boolean(options = {}) { return mergeSchemaOptions({ type: "boolean" }, options); },
  Literal(value, options = {}) { return mergeSchemaOptions({ const: value, enum: [value], type: literalType(value) }, options); },
  Array(items = {}, options = {}) { return mergeSchemaOptions({ type: "array", items }, options); },
  Union(items = [], options = {}) { return mergeSchemaOptions({ anyOf: items }, options); },
  Optional(schema = {}) { return markOptional(schema); },
  Record(_keySchema = {}, valueSchema = {}, options = {}) {
    return mergeSchemaOptions({ type: "object", additionalProperties: stripOptional(valueSchema) }, options);
  },
  Object(properties = {}, options = {}) {
    const normalized = {};
    const required = [];
    for (const [key, value] of Object.entries(properties || {})) {
      const propertySchema = stripOptional(value);
      normalized[key] = propertySchema;
      if (!value || value.__tauOptional !== true) required.push(key);
    }
    const schema = { type: "object", properties: normalized };
    if (required.length > 0) schema.required = required;
    return mergeSchemaOptions(schema, options);
  }
};
export default { Type };
`;

const piEventStreamModuleSource = String.raw`
/** 【AI】【事件流】保存异步生产者输出，并支持终值等待 */
export class EventStream {
  /** @param {Function} isComplete 终止判定 @param {Function} extractResult 终值提取 */
  constructor(isComplete, extractResult) {
    this.queue = []; this.offset = 0; this.waiting = []; this.done = false;
    this.isComplete = isComplete; this.extractResult = extractResult;
    this.finalResult = new Promise(resolve => { this.resolveResult = resolve; });
  }
  /** @param {*} event 事件 @returns {void} 顺序投递 */
  push(event) {
    if (this.done) return;
    if (this.isComplete(event)) { this.done = true; this.resolveResult(this.extractResult(event)); }
    const waiter = this.waiting.shift();
    if (waiter) waiter({ value: event, done: false }); else this.queue.push(event);
    if (this.done) for (const waiting of this.waiting.splice(0)) waiting({ done: true });
  }
  /** @param {*} result 可选终值 @returns {void} 结束消费 */
  end(result) {
    this.done = true;
    if (result !== undefined) this.resolveResult(result);
    for (const waiting of this.waiting.splice(0)) waiting({ done: true });
  }
  /** @returns {Promise<*>} 最终结果 */
  result() { return this.finalResult; }
  /** @returns {AsyncIterator<*>} 按原顺序消费所有事件 */
  async *[Symbol.asyncIterator]() {
    while (true) {
      if (this.offset < this.queue.length) {
        const event = this.queue[this.offset++];
        if (this.offset === this.queue.length) { this.queue = []; this.offset = 0; }
        yield event;
      } else if (this.done) return;
      else { const item = await new Promise(resolve => this.waiting.push(resolve)); if (item.done) return; yield item.value; }
    }
  }
}
/** 【AI】【助手流】终值来自 done.message 或 error.error */
export class AssistantMessageEventStream extends EventStream {
  /** 创建助手事件流 */
  constructor() { super(event => event.type === "done" || event.type === "error", event => event.type === "done" ? event.message : event.error); }
}
/** 【AI】【助手流】@returns {AssistantMessageEventStream} 新助手流 */
export function createAssistantMessageEventStream() { return new AssistantMessageEventStream(); }
`;

const piAiModuleSource = typeBoxModuleSource + piEventStreamModuleSource + String.raw`
const apiProviders = new Map();
const modelRegistry = new Map();
const oauthRegistryKey = Symbol.for("@tau/pi-ai/oauth-registry");
const oauthProviders = globalThis[oauthRegistryKey] ??= new Map();
export function registerApiProvider(provider, sourceId) {
  if (provider && provider.api) apiProviders.set(provider.api, { provider, sourceId });
}
export function getApiProvider(api) { return apiProviders.get(api)?.provider; }
export function getApiProviders() { return Array.from(apiProviders.values(), entry => entry.provider); }
export function unregisterApiProviders(sourceId) {
  for (const [api, entry] of apiProviders.entries()) if (entry.sourceId === sourceId) apiProviders.delete(api);
}
export function clearApiProviders() { apiProviders.clear(); }
export function registerModel(provider, model) {
  if (!modelRegistry.has(provider)) modelRegistry.set(provider, new Map());
  modelRegistry.get(provider).set(model.id, { ...model, provider });
}
export function getModel(provider, modelId) { return modelRegistry.get(provider)?.get(modelId); }
export function getProviders() { return Array.from(modelRegistry.keys()); }
export function getModels(provider) { return Array.from(modelRegistry.get(provider)?.values() ?? []); }
export function calculateCost(model, usage) {
  const cost = usage.cost ?? {};
  const rates = model?.cost ?? {};
  cost.input = ((rates.input ?? 0) / 1000000) * (usage.input ?? 0);
  cost.output = ((rates.output ?? 0) / 1000000) * (usage.output ?? 0);
  cost.cacheRead = ((rates.cacheRead ?? 0) / 1000000) * (usage.cacheRead ?? 0);
  cost.cacheWrite = ((rates.cacheWrite ?? 0) / 1000000) * (usage.cacheWrite ?? 0);
  cost.total = cost.input + cost.output + cost.cacheRead + cost.cacheWrite;
  usage.cost = cost;
  return cost;
}
export function supportsXhigh(model) {
  const id = String(model?.id ?? "");
  return id.includes("gpt-5.2") || id.includes("gpt-5.3") || id.includes("gpt-5.4") ||
    id.includes("opus-4-6") || id.includes("opus-4.6") ||
    id.includes("opus-4-7") || id.includes("opus-4.7");
}
export function modelsAreEqual(a, b) { return !!a && !!b && a.id === b.id && a.provider === b.provider; }
export function getOAuthProvider(id) { return oauthProviders.get(id); }
export function registerOAuthProvider(provider) { if (provider && provider.id) oauthProviders.set(provider.id, provider); }
export function unregisterOAuthProvider(id) { oauthProviders.delete(id); }
export function resetOAuthProviders() { oauthProviders.clear(); }
export function getOAuthProviders() { return Array.from(oauthProviders.values()); }
export function getOAuthProviderInfoList() {
  return getOAuthProviders().map(provider => ({ id: provider.id, name: provider.name, available: true }));
}
export async function refreshOAuthToken(providerId, credentials) {
  const provider = getOAuthProvider(providerId);
  if (!provider || typeof provider.refreshToken !== "function") throw new Error("Unknown OAuth provider: " + providerId);
  return provider.refreshToken(credentials);
}
export async function getOAuthApiKey(providerId, credentials) {
  const provider = getOAuthProvider(providerId);
  const credential = credentials ? credentials[providerId] : undefined;
  if (!provider || !credential) return null;
  if (typeof provider.getApiKey !== "function") return null;
  return { newCredentials: credential, apiKey: provider.getApiKey(credential) };
}
`;

const piAiOAuthModuleSource = String.raw`
const oauthRegistryKey = Symbol.for("@tau/pi-ai/oauth-registry");
const providers = globalThis[oauthRegistryKey] ??= new Map();
export function getOAuthProvider(id) { return providers.get(id); }
export function registerOAuthProvider(provider) { if (provider && provider.id) providers.set(provider.id, provider); }
export function unregisterOAuthProvider(id) { providers.delete(id); }
export function resetOAuthProviders() { providers.clear(); }
export function getOAuthProviders() { return Array.from(providers.values()); }
export function getOAuthProviderInfoList() {
  return getOAuthProviders().map(provider => ({ id: provider.id, name: provider.name, available: true }));
}
export async function refreshOAuthToken(providerId, credentials) {
  const provider = getOAuthProvider(providerId);
  if (!provider || typeof provider.refreshToken !== "function") throw new Error("Unknown OAuth provider: " + providerId);
  return provider.refreshToken(credentials);
}
export async function getOAuthApiKey(providerId, credentials) {
  const provider = getOAuthProvider(providerId);
  const credential = credentials ? credentials[providerId] : undefined;
  if (!provider || !credential) return null;
  if (typeof provider.getApiKey !== "function") return null;
  return { newCredentials: credential, apiKey: provider.getApiKey(credential) };
}
`;

const piAgentCoreModuleSource = String.raw`
export const ToolExecutionMode = Object.freeze({ Sequential: "sequential", Parallel: "parallel" });
export class EventStream {
  constructor() { this.events = []; }
  push(event) { this.events.push(event); }
  async *[Symbol.asyncIterator]() { for (const event of this.events) yield event; }
}
export class Agent {
  constructor(options = {}) { this.options = options; }
}
`;

const piTuiModuleSource = String.raw`
export class Container {
  constructor() { this.children = []; }
  addChild(child) { this.children.push(child); return child; }
  clear() { this.children = []; }
  render(width = 80) { return this.children.flatMap(child => typeof child?.render === "function" ? child.render(width) : []); }
}
export class Text {
  constructor(text = "", paddingX = 1, paddingY = 1) { this.text = String(text ?? ""); this.paddingX = paddingX; this.paddingY = paddingY; }
  setText(text) { this.text = String(text ?? ""); }
  render() { return this.text.trim().length === 0 ? [] : [this.text]; }
}
export class Spacer { constructor(lines = 1) { this.lines = lines; } render() { return Array.from({ length: Math.max(0, this.lines) }, () => ""); } }
export class Box extends Container {}
export class Markdown extends Text {}
export class TruncatedText extends Text {}
export class Input extends Text {}
export class Loader extends Text {}
export class CancellableLoader extends Loader {}
export class SelectList extends Container {}
export class SettingsList extends Container {}
export class Image extends Text {}
export class TUI {}
export class ProcessTerminal {}
export class KeybindingsManager { matches() { return false; } }
export const CURSOR_MARKER = "";
export const TUI_KEYBINDINGS = {};
export function getKeybindings() { return new KeybindingsManager(); }
export function setKeybindings() {}
export function matchesKey(actual, expected) { return actual === expected; }
export function parseKey(value) { return String(value ?? ""); }
export function visibleWidth(value) { return String(value ?? "").replace(/\x1b\[[0-9;]*m/g, "").length; }
export function truncateToWidth(value, width) { return String(value ?? "").slice(0, Math.max(0, width)); }
export function wrapTextWithAnsi(value) { return [String(value ?? "")]; }
export function fuzzyMatch(query, value) { return String(value ?? "").toLowerCase().includes(String(query ?? "").toLowerCase()) ? { score: 1 } : undefined; }
export function fuzzyFilter(items, query) { return (items ?? []).filter(item => fuzzyMatch(query, String(item))); }
export function getCapabilities() { return {}; }
export function getImageDimensions() { return undefined; }
export function imageFallback() { return ""; }
`;

const piCodingAgentModuleSource = String.raw`
export function defineTool(tool) { return tool; }
export function createEventBus() {
  const listeners = new Map();
  return {
    on(type, handler) {
      const list = listeners.get(type) ?? [];
      list.push(handler);
      listeners.set(type, list);
      return () => listeners.set(type, (listeners.get(type) ?? []).filter(candidate => candidate !== handler));
    },
    async emit(type, payload) {
      for (const handler of listeners.get(type) ?? []) await handler(payload);
    }
  };
}
export class ModelRegistry {}
export class SessionManager {}
export class CustomEditor {
  constructor(...args) { this.args = args; this.actionHandlers = new Map(); }
  onAction(action, handler) { this.actionHandlers.set(action, handler); }
  handleInput() {}
  getText() { return ""; }
  isShowingAutocomplete() { return false; }
  render() { return []; }
  dispose() {}
}
export function createSyntheticSourceInfo(path, options = {}) { return { path, ...options }; }
`;

const virtualModuleSources = new Map([
  ["@sinclair/typebox", typeBoxModuleSource],
  ["@mariozechner/pi-agent-core", piAgentCoreModuleSource],
  ["@mariozechner/pi-tui", piTuiModuleSource],
  ["@mariozechner/pi-ai", piAiModuleSource],
  ["@mariozechner/pi-ai/oauth", piAiOAuthModuleSource],
  ["@mariozechner/pi-ai/utils/event-stream", piEventStreamModuleSource],
  ["@mariozechner/pi-ai/compat", piAiModuleSource],
  ["@mariozechner/pi-coding-agent", piCodingAgentModuleSource]
]);

/**
 * 【CodingAgent】【扩展导入】解析当前及历史包名，并让别名共享模块实例
 * @param {string} specifier 导入说明符
 * @returns {string|undefined} 虚拟模块 URL，未知包交给 Node 处理
 */
function virtualModuleUrl(specifier) {
  // 1. 【CodingAgent】【包名兼容】新旧包名共用相同 URL，避免工具、模型和认证注册表被重复初始化
  if (specifier.startsWith("@earendil-works/pi-")) specifier = specifier.replace("@earendil-works/", "@mariozechner/");
  if (specifier === "typebox") specifier = "@sinclair/typebox";
  const source = virtualModuleSources.get(specifier);
  return source === undefined
    ? undefined
    : "data:text/javascript;charset=utf-8," + encodeURIComponent(source);
}

function hasModuleSyntax(source) {
  return /(^|\s)(import|export)\s/.test(source);
}

function readNearestPackageType(directory) {
  let current = directory;
  while (current && current !== path.dirname(current)) {
    const packageJsonPath = path.join(current, "package.json");
    try {
      const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
      if (packageJson && packageJson.type === "module") return "module";
      if (packageJson && packageJson.type === "commonjs") return "commonjs";
    } catch {
    }
    current = path.dirname(current);
  }
  return undefined;
}

function inferTypeScriptFormat(url, source) {
  const packageType = readNearestPackageType(path.dirname(fileURLToPath(url)));
  if (packageType === "module" || packageType === "commonjs") return packageType;
  return hasModuleSyntax(source) ? "module" : "commonjs";
}

function installExtensionImportHook(requireHooks) {
  if (extensionImportHookInstalled) return;
  if (typeof moduleApi.registerHooks !== "function") {
    if (requireHooks) {
      throw new Error("typescript extension runtime unavailable: Node.js module hooks are not available");
    }
    return;
  }

  moduleApi.registerHooks({
    resolve(specifier, context, nextResolve) {
      const url = virtualModuleUrl(specifier);
      if (url) {
        return { url, shortCircuit: true };
      }
      return nextResolve(specifier, context);
    },
    load(url, context, nextLoad) {
      const parsed = new URL(url);
      if (parsed.protocol === "file:" && parsed.pathname.toLowerCase().endsWith(".ts")) {
        if (typeof moduleApi.stripTypeScriptTypes !== "function") {
          throw new Error("typescript extension runtime unavailable: Node.js type stripping hooks are not available");
        }
        const source = fs.readFileSync(parsed, "utf8");
        const stripped = moduleApi.stripTypeScriptTypes(source, { mode: "strip", sourceUrl: url });
        return {
          format: inferTypeScriptFormat(url, source),
          shortCircuit: true,
          source: stripped
        };
      }

      return nextLoad(url, context);
    }
  });
  extensionImportHookInstalled = true;
}

function toText(value) {
  if (value === undefined || value === null) return "";
  if (typeof value === "string") return value;
  if (Array.isArray(value)) {
    return value.map(item => toText(item)).filter(Boolean).join("\n");
  }
  if (typeof value === "object") {
    if (typeof value.text === "string") return value.text;
    if (typeof value.content === "string") return value.content;
    if (typeof value.message === "string") return value.message;
    return JSON.stringify(value);
  }
  return String(value);
}

function normalizeContentBlocks(value) {
  if (!Array.isArray(value)) return [];
  return value.map(item => {
    if (item && typeof item === "object") return item;
    return { type: "text", text: toText(item) };
  });
}

function normalizeMessageContent(value) {
  if (Array.isArray(value)) return normalizeContentBlocks(value);
  if (value && typeof value === "object" && typeof value.type === "string") return normalizeContentBlocks([value]);
  if (value === undefined || value === null) return [];
  return [{ type: "text", text: toText(value) }];
}

/**
 * 【CodingAgent】【消息转换】规范化扩展消息，同时保留系统声明增量
 * @param {object} value 扩展消息
 * @param {string|undefined} fallbackRole 缺省角色
 * @returns {object|undefined} 可传回宿主的消息
 */
function normalizeAgentMessage(value, fallbackRole = undefined) {
  if (!value || typeof value !== "object") return undefined;
  const role = String(value.role ?? fallbackRole ?? "");
  if (!role) return undefined;
  const message = {
    ...value,
    role,
    content: normalizeMessageContent(value.content)
  };
  if (role === "system") {
    // 1. 【CodingAgent】【系统声明】不得在 message_end 回调往返时丢弃工具与段落
    if (typeof value.content === "string") message.content = value.content;
    if (value.sections && typeof value.sections === "object") message.sections = value.sections;
    if (Array.isArray(value.toolsAdded)) message.toolsAdded = value.toolsAdded;
    if (Array.isArray(value.toolsRemoved)) message.toolsRemoved = value.toolsRemoved;
    message.timestamp = typeof value.timestamp === "number" ? value.timestamp : Date.now();
  } else if (role === "custom") {
    message.customType = String(value.customType ?? "");
    message.display = value.display !== false;
    if (Object.prototype.hasOwnProperty.call(value, "details")) message.details = value.details;
    message.timestamp = typeof value.timestamp === "number" ? value.timestamp : Date.now();
  } else if (role === "toolResult") {
    message.toolCallId = String(value.toolCallId ?? "");
    message.isError = value.isError === true;
    if (typeof value.toolName === "string") message.toolName = value.toolName;
  } else if (role === "assistant") {
    if (typeof value.stopReason === "string") message.stopReason = value.stopReason;
    if (typeof value.errorMessage === "string") message.errorMessage = value.errorMessage;
  }
  return message;
}

/**
 * 【CodingAgent】【上下文保护】重放系统段落和工具声明，在普通上下文裁剪后保留完整系统状态
 * @param {object[]} messages 完整会话消息
 * @returns {object|undefined} 合并后的开场系统消息
 */
function replaySystemMessage(messages) {
  const systems = messages.filter(message => message.role === "system");
  if (!systems.length) return undefined;
  const sections = new Map(), tools = new Map(), content = [];
  for (const message of systems) {
    const text = typeof message.content === "string" ? message.content : (message.content ?? []).map(block => block.text ?? "").join("\n");
    if (text) content.push(text);
    for (const [name, value] of Object.entries(message.sections ?? {})) {
      if (value === null) sections.delete(name); else sections.set(name, value);
    }
    for (const tool of message.toolsRemoved ?? []) tools.delete(typeof tool === "string" ? tool : tool.name);
    for (const tool of message.toolsAdded ?? []) tools.set(tool.name, tool);
  }
  return {
    role: "system", content: content.join("\n\n"), timestamp: systems[0].timestamp,
    ...(sections.size ? { sections: Object.fromEntries(sections) } : {}),
    ...(tools.size ? { toolsAdded: [...tools.values()] } : {})
  };
}

/**
 * 【CodingAgent】【上下文钩子】按注册顺序处理单个模块的上下文，普通钩子无法误删系统状态
 * @param {object} event 输入事件
 * @param {Function[]} handlers 已注册处理器快照
 * @param {object} context 扩展上下文
 * @param {string[]} errors 错误收集器
 * @returns {Promise<object>} 完整转换事件
 */
async function transformContextEvent(event, handlers, context, errors) {
  let messages = event.messages;
  for (const handler of handlers.slice()) {
    try {
      if (event.type === "context") {
        const visible = messages.filter(message => message.role !== "system");
        const snapshot = visible.slice();
        const result = await handler({ type: event.type, messages: visible }, context);
        const returned = result?.messages ?? visible;
        if (!Array.isArray(returned)) throw Error("Context messages must be an array.");
        if (returned.length === snapshot.length && returned.every((message, index) => message === snapshot[index])) continue;
        const head = replaySystemMessage(messages);
        messages = head ? [head, ...returned] : returned;
      } else {
        const hadHead = messages[0]?.role === "system";
        const result = await handler({ type: event.type, messages }, context);
        if (result?.messages !== undefined) {
          if (!Array.isArray(result.messages)) throw Error("Context messages must be an array.");
          messages = result.messages;
        }
        if (hadHead && messages[0]?.role !== "system") errors.push("Handler removed the leading system message; the request has no prompt or initial tool declarations.");
      }
    } catch (error) { errors.push(formatError(error)); }
  }
  return { ...event, messages };
}

/**
 * 【CodingAgent】【启动提示】同步重建可变选项，让同一处理器内的 getter 立即反映修改
 * @param {object} options 宿主规范化后的完整提示选项
 * @param {object} defaults 宿主提供的产品前言和安装目录文档
 * @returns {object} 与宿主构建器一致的有序段落
 */
function buildPromptSections(options, defaults) {
  for (const name of Object.keys(options.sections)) {
    if (!/^[a-z][a-z0-9_-]*$/.test(name) || name === "preamble") throw Error("Invalid system prompt section name: " + name);
  }
  const sections = {};
  if (options.customPrompt) sections.preamble = options.customPrompt;
  else {
    sections.preamble = defaults.preamble;
    const tools = options.selectedTools.filter(name => options.toolSnippets[name]).map(name => "- " + name + ": " + options.toolSnippets[name]);
    sections.tools = (tools.length ? tools.join("\n") : "(none)") + "\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.";
    const rules = [];
    const bash = options.selectedTools.includes("bash") || options.selectedTools.includes("shell");
    const powershell = options.selectedTools.includes("powershell");
    if ((bash || powershell) && !options.selectedTools.some(name => ["grep", "find", "glob", "ls"].includes(name))) {
      rules.push(bash && powershell ? "Use bash or PowerShell for file operations like listing, searching, and finding files" :
        powershell ? "Use PowerShell for file operations like listing, searching, and finding files" : "Use bash for file operations like ls, rg, find");
    }
    for (const name of options.selectedTools) rules.push(...(options.toolGuidelines[name] ?? []));
    rules.push(...options.promptGuidelines, "Be concise in your responses", "Show file paths clearly when working with files");
    sections.rules = [...new Set(rules.map(rule => rule.trim()).filter(Boolean))].map(rule => "- " + rule).join("\n");
    sections.docs = defaults.docs;
  }
  if (options.appendSystemPrompt) sections.addendum = options.appendSystemPrompt;
  if (options.contextFiles.length) sections.project_context = "Project-specific instructions and guidelines:\n\n" +
    options.contextFiles.map(file => '<project_instructions path="' + file.path.replace(/\\/g, "/") + '">\n' + file.content + "\n</project_instructions>").join("\n\n");
  const readTool = ["read", "read_file", "bash", "shell"].find(name => options.selectedTools.includes(name));
  const skills = options.skills.filter(skill => !skill.disableModelInvocation);
  if (readTool && skills.length) {
    /** 【CodingAgent】【技能提示】@param {string} value 原始字段 @returns {string} XML 转义文本 */
    const escape = value => value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&apos;");
    const lines = [
      "The following skills provide specialized instructions for specific tasks.",
      ["bash", "shell"].includes(readTool) ? "Use " + readTool + " to load a skill's file when the task matches its description." :
        "Use the " + readTool + " tool to load a skill's file when the task matches its description.",
      "When a skill file references a relative path, resolve it against the skill directory and use that absolute path in tool commands.",
      "", "<available_skills>"
    ];
    for (const skill of skills) lines.push("  <skill>", "    <name>" + escape(skill.name) + "</name>",
      "    <description>" + escape(skill.description) + "</description>", "    <location>" + escape(skill.filePath) + "</location>", "  </skill>");
    lines.push("</available_skills>");
    sections.skills = lines.join("\n");
  }
  sections.cwd = options.cwd.replace(/\\/g, "/");
  for (const [name, content] of Object.entries(options.sections)) if (content) sections[name] = content;
  for (const name of Object.keys(sections)) if (name !== "preamble") sections[name] = "<" + name + ">\n" + sections[name] + "\n</" + name + ">";
  return sections;
}

function normalizeCustomMessageContent(value) {
  if (typeof value === "string") return value;
  if (Array.isArray(value)) return normalizeContentBlocks(value);
  return toText(value);
}

/**
 * 【CodingAgent】【工具结果】保留文字、图片、空内容及详情，兼容旧式字符串返回值
 * @param {*} value 扩展执行结果或中间更新
 * @returns {object} 可序列化的工具结果
 */
function normalizeToolResult(value) {
  if (value && typeof value === "object") {
    const content = Object.prototype.hasOwnProperty.call(value, "content")
      ? normalizeMessageContent(value.content)
      : normalizeMessageContent(value);
    return {
      content,
      isError: value.isError === true,
      details: Object.prototype.hasOwnProperty.call(value, "details") ? value.details : undefined,
      structuredContent: value.structuredContent,
      usage: value.usage,
      terminate: value.terminate === true
    };
  }

  return {
    content: normalizeMessageContent(value),
    isError: false,
    details: undefined
  };
}

function normalizeStringArray(value) {
  if (value === undefined || value === null) return undefined;
  if (!Array.isArray(value)) return undefined;
  return value.map(item => toText(item));
}

const themeProxy = {
  fg(_name, text) { return toText(text); },
  bg(_name, text) { return toText(text); },
  style(_name, text) { return toText(text); },
  bold(text) { return toText(text); },
  dim(text) { return toText(text); },
  italic(text) { return toText(text); },
  underline(text) { return toText(text); }
};

/** 【CodingAgent】【组件文本】@param {*} v 组件、文本或文本数组 @param {number} width 组件列数 @returns {string[]|undefined} 渲染后的文本行，空组件返回未定义 */
function normalizeComponentLines(v, width = 120) {
  if (v == null) return undefined;
  if (typeof v === "function") v = v(undefined, themeProxy, {});
  if (v && typeof v.render === "function") v = v.render(width);
  if (Array.isArray(v)) return v.map(toText);
  const text = toText(v);
  return text ? text.split(/\r?\n/) : [];
}

function normalizeIndicator(o) {
  if (o == null) return { frames: undefined, intervalMs: undefined };
  const frames = normalizeStringArray(o.frames) ?? [];
  const n = Number(o.intervalMs);
  return { frames, intervalMs: Number.isFinite(n) ? Math.max(0, Math.trunc(n)) : undefined };
}

const supportedEventNames = new Set([
  "project_trust",
  "resources_discover",
  "input",
  "user_bash",
  "model_select",
  "thinking_level_select",
  "session_info_changed",
  "mcp_servers_change",
  "before_agent_start",
  "context",
  "context_with_system",
  "before_provider_request",
  "before_provider_headers",
  "after_provider_response",
  "provider_stream_event",
  "tool_call",
  "tool_result",
  "session_start",
  "session_shutdown",
  "session_before_switch",
  "session_before_fork",
  "session_before_tree",
  "session_tree",
  "session_before_compact",
  "session_compact",
  "session_compact_failed",
  "agent_start",
  "agent_end",
  "agent_before_settle",
  "agent_settled",
  "cache_warming_decision",
  "turn_start",
  "turn_end",
  "message_start",
  "message_update",
  "message_end",
  "tool_execution_start",
  "tool_execution_update",
  "tool_execution_end",
  "ui_prompt_start",
  "ui_prompt_end"
]);

function addHandler(handlerMap, unsupported, eventName, handler) {
  const key = String(eventName ?? "");
  if (!supportedEventNames.has(key) || typeof handler !== "function") {
    unsupported.handlers++;
    return () => {};
  }

  const handlers = handlerMap.get(key) ?? [];
  handlers.push(handler);
  handlerMap.set(key, handlers);
  return () => {
    const current = handlerMap.get(key) ?? [];
    const index = current.indexOf(handler);
    if (index >= 0) current.splice(index, 1);
  };
}

/**
 * 【CodingAgent】【扩展注册】创建持久注册 API，消息与标志值从当前请求获取
 * @param {Map} commandMap 命令注册表
 * @param {Map} toolMap 工具注册表
 * @param {Map} flagMap 标志注册表
 * @param {Map} shortcutMap 快捷键注册表
 * @param {Map} flagValues 默认标志值
 * @param {Map} handlerMap 事件注册表
 * @param {Map} messageRendererMap 消息渲染器注册表
 * @param {Map} entryRendererMap 自定义条目渲染器注册表
 * @param {object} markdownRegistration 当前模块的 Markdown 转换器容器
 * @param {object} unsupported 未支持注册计数
 * @param {object} payload 初始化请求参数
 * @returns {object} 扩展 API
 */
function createApi(commandMap, toolMap, flagMap, shortcutMap, flagValues, handlerMap, messageRendererMap, entryRendererMap, markdownRegistration, unsupported, payload) {
  const recordMessage = (value, options = undefined) => {
    if (value && typeof value === "object" && !Array.isArray(value) && Object.prototype.hasOwnProperty.call(value, "customType")) {
      const delivery = options && typeof options === "object" ? options : {};
      const deliverAs = delivery.deliverAs === "steer" || delivery.deliverAs === "followUp" || delivery.deliverAs === "nextTurn"
        ? delivery.deliverAs
        : undefined;
      recordAction({
        type: "customMessage",
        customType: String(value.customType ?? ""),
        content: Object.prototype.hasOwnProperty.call(value, "content") ? normalizeCustomMessageContent(value.content) : "",
        display: value.display !== false,
        details: Object.prototype.hasOwnProperty.call(value, "details") ? value.details : undefined,
        triggerTurn: typeof delivery.triggerTurn === "boolean" ? delivery.triggerTurn : undefined,
        deliverAs,
        timestamp: Date.now()
      }, payload.filePath);
      return;
    }

    const message = toText(value);
    if (message.trim().length > 0) recordAction({ type: "sendMessage", message }, payload.filePath);
  };
  const api = {
    registerCommand(name, options = {}) {
      const key = String(name ?? "");
      commandMap.set(key, { name: key, options: options || {} });
    },
    /** 【CodingAgent】【工具注册】@param {object} tool 工具定义 @returns {void} 更新持久定义并同步已绑定会话 */
    registerTool(tool = {}) {
      const key = String(tool && tool.name !== undefined ? tool.name : "");
      if (typeof tool?.parameters !== "object" || tool.parameters === null || Array.isArray(tool.parameters))
        throw new Error(`Tool "${key}" registered by extension "${payload.filePath}" must define an object parameter schema.`);
      toolMap.set(key, tool || {});
      if (!requestContext.getStore()?.sessionReady) return;
      const runtime = readRuntime();
      if (runtime.extensionToolsEnabled === false || runtime.excludedTools?.includes(key) || Array.isArray(runtime.allowedTools) && !runtime.allowedTools.includes(key)) return;
      // 1. 【CodingAgent】【注册优先级】扩展按加载顺序取同名定义，重注册保留目录位置
      const owner = [...readyExtensions].find(([, state]) => state.toolMap.has(key));
      if (!owner) return;
      const info = { ...serializeToolDefinition(owner[1].toolMap.get(key)), extensionPath: owner[0], sourceInfo: currentPayload().extensionSources?.[owner[0]] };
      const index = runtime.tools.findIndex(item => item.name === key);
      const previous = runtime.tools[index];
      if (previous?.sourceInfo?.source === "sdk") return;
      if (index < 0) runtime.tools.push(info); else runtime.tools[index] = info;
      if (runtime.allowedTools?.includes(key) && ["direct", "model-only"].includes(info.exposure) ||
        isToolActiveOnRegistration(info) && !isToolActiveOnRegistration(previous)) runtime.activeTools.push(key);
      runtime.activeTools.push(...(runtime.pendingTools ?? []));
      const changes = prepareRuntimeToolLoadout(runtime);
      runtime.pendingTools = (runtime.pendingTools ?? []).filter(name => !runtime.activeTools.includes(name));
      emitRuntimeOperation("registerTool", { filePath: owner[0], tool: info, changes });
    },
    registerFlag(name, options = {}) {
      const key = String(name ?? "");
      const flagOptions = options || {};
      flagMap.set(key, { name: key, options: flagOptions });
      if (Object.prototype.hasOwnProperty.call(flagOptions, "default") && !flagValues.has(key)) {
        const defaultValue = flagOptions.default;
        if (typeof defaultValue === "boolean" || typeof defaultValue === "string") {
          flagValues.set(key, defaultValue);
        }
      }
    },
    registerShortcut(shortcut, options = {}) {
      const key = String(shortcut ?? "");
      shortcutMap.set(key, { shortcut: key, options: options || {} });
    },
    registerMessageRenderer(customType, renderer) {
      const key = String(customType ?? "");
      messageRendererMap.set(key, renderer);
    },
    /** 【CodingAgent】【条目渲染注册】@param {string} customType 条目类型 @param {Function} renderer 渲染函数 @returns {void} 同模块同名注册以后者为准 */
    registerEntryRenderer(customType, renderer) {
      entryRendererMap.set(String(customType ?? ""), renderer);
    },
    /** 【CodingAgent】【Markdown 注册】@param {Function} transformer 同步显示转换器 @returns {void} 同模块最后一次注册覆盖此前值 */
    registerMarkdownTransformer(transformer) {
      markdownRegistration.transformer = transformer;
    },
    /** 【CodingAgent】【提供方注册】@param {string|object} name 标识或提供方实例 @param {object} config 扩展配置 @returns {void} 注册会话提供方 */
    registerProvider(name, config) {
      const canonical = typeof name === "object" && name !== null ? name : undefined;
      const id = canonical?.id ?? name;
      if (typeof id !== "string" || !id.trim()) throw new Error("Provider id is required.");
      if (canonical) config = { name: canonical.name, baseUrl: canonical.baseUrl, headers: canonical.headers, models: canonical.getAllModels?.() ?? canonical.getModels?.() ?? [],
        streamSimple: canonical.streamSimple?.bind(canonical), stream: canonical.stream?.bind(canonical), refreshModels: canonical.refreshModels?.bind(canonical),
        oauth: canonical.auth?.oauth ? adaptCanonicalOAuth(canonical.auth.oauth) : undefined, canonical };
      if (!config || typeof config !== "object" || Array.isArray(config)) throw new Error(`Provider '${id}' requires a configuration object.`);
      if (config.models !== undefined && !Array.isArray(config.models)) throw new Error(`Provider '${id}': models must be an array.`);
      const runtime = requestContext.getStore()?.sessionReady ? readRuntime() : undefined;
      validateProviderConfig(id, config, runtime?.providerBaselines?.[id] ?? runtime?.models.filter(model => model.provider === id) ?? currentPayload().providerDefaults?.[id] ?? []);
      if (!canonical) {
        const previous = extensionProviders.get(id);
        if (previous && !previous.config.canonical) config = { ...(previous.registeredConfig ?? previous.config), ...Object.fromEntries(Object.entries(config).filter(([, value]) => value !== undefined)) };
      }
      if (runtime) updateProviderSnapshot(runtime, id, config);
      mutateProviderRegistration(id, { id, config, registeredConfig: config, filePath: payload.filePath });
      if (runtime) emitRuntimeOperation("registerProviders", { providers: serializeProviders() });
      if (runtime && (typeof config.refreshModels === "function" || typeof config.oauth?.modifyModels === "function" || config.canonical?.auth?.apiKey)) void requestHost("refresh", { providers: [id], allowNetwork: false }).catch(() => {});
    },
    /** 【CodingAgent】【提供方注销】@param {string} name 标识 @returns {void} 恢复内置及文件配置 */
    unregisterProvider(name) {
      const runtime = requestContext.getStore()?.sessionReady ? readRuntime() : undefined;
      if (!extensionProviders.has(name)) return;
      if (runtime) updateProviderSnapshot(runtime, name, undefined);
      mutateProviderRegistration(name, undefined);
      if (runtime) emitRuntimeOperation("registerProviders", { providers: serializeProviders() });
    },
    /** 【CodingAgent】【虚拟注册】@param {object} definition 模型与路由 @returns {void} 注册独立虚拟目录项 */
    registerVirtualModel(definition) { registerVirtualModel(definition, payload.filePath); },
    /** 【CodingAgent】【虚拟注销】@param {string} provider 提供方 @param {string} id 标识 @returns {void} 注销路由 */
    unregisterVirtualModel(provider, id) { unregisterVirtualModel(provider, id); },
    /** 【CodingAgent】【MCP 注册】@param {string} name 服务器名 @param {object} config 配置 @returns {void} 校验并注册当前扩展的服务器 */
    registerMcpServer(name, config) { registerMcpServer(name, config, payload.filePath); },
    /** 【CodingAgent】【MCP 注销】@param {string} name 服务器名 @returns {void} 仅移除本扩展拥有的服务器 */
    unregisterMcpServer(name) { if (mcpServers.get(name)?.extensionPath === payload.filePath) mutateMcpServer(name, undefined); },
    /** 【CodingAgent】【MCP 查询】@returns {object[]} 按注册顺序返回独立服务器快照 */
    getMcpServers() { return cloneSessionValue([...mcpServers.values()]); },
    on(eventName, handler) { return addHandler(handlerMap, unsupported, eventName, handler); },
    getFlag(name) {
      const key = String(name ?? "");
      if (!flagMap.has(key)) return undefined;
      const overrides = requestContext.getStore()?.payload.flagValues;
      if (overrides && Object.prototype.hasOwnProperty.call(overrides, key)) return overrides[key];
      return flagValues.get(key);
    },
    sendMessage: recordMessage,
    /** 【CodingAgent】【用户消息】@param {string|object[]} content 用户文本或内容块 @param {object} options 投递和展开选项 @returns {void} 记录完整用户消息动作 */
    sendUserMessage(content, options = {}) {
      recordAction({ type: "userMessage", content: normalizeMessageContent(content),
        deliverAs: options.deliverAs, expandPromptTemplates: options.expandPromptTemplates === true, timestamp: Date.now() }, payload.filePath);
    },
    /** 【CodingAgent】【扩展状态】@param {string} customType 状态类型 @param {*} data JSON 数据 @returns {void} 无返回值 */
    appendEntry(customType, data) {
      if (typeof customType !== "string" || !customType.trim()) throw new Error("Custom session entry type is required.");
      emitSessionEntry("custom", { customType, data });
    },
    /** 【CodingAgent】【会话名称】@param {string} name 新名称，空白表示清除 @returns {void} 无返回值 */
    setSessionName(name) {
      if (typeof name !== "string") throw new Error("Session name must be a string.");
      emitSessionEntry("session_info", { name: name.replace(/[\r\n]+/g, " ").trim() || undefined });
    },
    /** 【CodingAgent】【会话名称】@returns {string|undefined} 当前名称 */
    getSessionName() { return readSession().name ?? undefined; },
    /** 【CodingAgent】【会话标签】@param {string} entryId 目标条目 @param {string|undefined} label 标签或清除 @returns {void} 无返回值 */
    setLabel(entryId, label) {
      const session = readSession();
      if (!session.entries.some(entry => entry.id === entryId)) throw new Error("Session label target does not exist.");
      if (label !== undefined && typeof label !== "string") throw new Error("Session label must be a string or undefined.");
      emitSessionEntry("label", { targetId: entryId, label: label?.trim() || undefined });
    },
    /** 【CodingAgent】【活动工具】@returns {string[]} 当前活动工具副本 */
    getActiveTools() { return [...readRuntime().activeTools]; },
    getAllTools() {
      if (requestContext.getStore()?.sessionReady) return cloneSessionValue(readRuntime().tools);
      return cloneSessionValue(Array.from(toolMap.values()).map(serializeToolDefinition));
    },
    /** 【CodingAgent】【活动工具】@param {string[]} names 所需工具 @returns {void} 无返回值 */
    setActiveTools(names) {
      if (!Array.isArray(names) || names.some(name => typeof name !== "string")) throw new Error("Tool names must be strings.");
      const runtime = readRuntime();
      const previous = runtime.activeTools;
      const aliases = { read_file: "read", write_file: "write", edit_file: "edit", shell: "bash", glob: "find" };
      names = names.map(name => runtime.tools.some(tool => tool.name === name) ? name : (aliases[name] ?? name));
      runtime.activeTools = names;
      const changes = prepareRuntimeToolLoadout(runtime);
      if (previous.some(name => !runtime.activeTools.includes(name))) runtime.pendingTools = [];
      emitRuntimeOperation("setActiveTools", { names, changes });
    },
    /** 【CodingAgent】【命令目录】@returns {object[]} 当前会话所有扩展、提示和技能命令的独立来源快照 */
    getCommands() { return cloneSessionValue(readRuntime().commands ?? []); },
    /** 【CodingAgent】【模型切换】@param {object} model 目标模型 @returns {Promise<boolean>} 是否成功切换 */
    setModel(model) {
      if (!model || typeof model.provider !== "string" || typeof model.id !== "string") return Promise.reject(new Error("Invalid model."));
      return requestHost("setModel", { provider: model.provider, modelId: model.id });
    },
    /** 【CodingAgent】【思考等级】@returns {string} 当前生效等级 */
    getThinkingLevel() { return readRuntime().thinkingLevel; },
    /** 【CodingAgent】【思考等级】@param {string} level 目标等级 @returns {void} 无返回值 */
    setThinkingLevel(level) {
      const levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
      if (!levels.includes(level)) throw new Error("Invalid thinking level.");
      const runtime = readRuntime();
      const supported = runtime.thinkingLevels;
      runtime.thinkingLevel = supported.includes(level) ? level : supported.find(item => levels.indexOf(item) >= levels.indexOf(level)) ?? supported.at(-1) ?? "off";
      emitRuntimeOperation("setThinkingLevel", { level });
    },
    /** 【CodingAgent】【扩展命令】@param {string} command 可执行文件 @param {string[]} args 参数 @param {object} options 目录、超时及取消选项 @returns {Promise<object>} 执行结果 */
    exec(command, args, options) { return executeExtensionCommand(command, args, payload.cwd, options); },
    events: {
      /** 【CodingAgent】【扩展事件】@param {string} channel 通道 @param {Function} handler 处理器 @returns {Function} 取消订阅函数 */
      on(channel, handler) {
        /** @param {*} data 事件数据 @returns {Promise<void>} 隔离处理器错误的任务 */
        const safeHandler = async data => {
          try { await handler(data); }
          catch (error) { console.error("【CodingAgent】【扩展事件】" + channel + ": " + formatError(error)); }
        };
        extensionEvents.on(channel, safeHandler);
        return () => extensionEvents.off(channel, safeHandler);
      },
      /** 【CodingAgent】【扩展事件】@param {string} channel 通道 @param {*} data 数据 @returns {void} 无返回值 */
      emit(channel, data) { extensionEvents.emit(channel, data); }
    },
    cwd: payload.cwd
  };
  /** 【CodingAgent】【实例隔离】@param {object} target API 对象或事件总线 @returns {object} 校验原实例的代理 */
  const bindApi = target => new Proxy(target, {
    /** 【CodingAgent】【实例隔离】@param {object} target 扩展 API @param {string|symbol} key 成员名 @returns {*} 与原实例绑定的成员 */
    get(target, key) {
      const value = Reflect.get(target, key);
      if (key === "events") return bindApi(value);
      if (typeof value !== "function") return value;
      return (...args) => {
        if (livePayload && payload.extensionGeneration !== livePayload.extensionGeneration) throw new Error("Extension instance is stale; use the withSession callback.");
        return value.apply(target, args);
      };
    }
  });
  return bindApi(api);
}

/**
 * 【CodingAgent】【扩展命令】执行独立子进程，收集 UTF-8 输出并处理超时和取消
 * @param {string} command 可执行文件
 * @param {string[]} args 参数列表，不经过 shell 解释
 * @param {string} cwd 会话工作目录
 * @param {object} options 可选目录、超时及 AbortSignal
 * @returns {Promise<object>} stdout、stderr、退出码及是否取消
 */
function executeExtensionCommand(command, args, cwd, options = {}) {
  const requestId = requestContext.getStore()?.id;
  if (requestId) process.stdout.write(waitPrefix + JSON.stringify({ requestId, paused: true }) + "\n");
  return new Promise(resolve => {
    let child, stdout = "", stderr = "", killed = false, finished = false, exited = false, exitCode = 0;
    let deadline, forceKill, idle;
    /** 【CodingAgent】【命令清理】@param {number} code 退出码 @returns {void} 释放管道并完成结果 */
    const finish = code => {
      if (finished) return;
      finished = true;
      clearTimeout(deadline); clearTimeout(forceKill); clearTimeout(idle);
      options.signal?.removeEventListener("abort", kill);
      child?.stdout?.destroy(); child?.stderr?.destroy();
      resolve({ stdout, stderr, code: code ?? 0, killed });
    };
    /** 【CodingAgent】【命令取消】@returns {void} 先正常终止，超时后强制终止 */
    const kill = () => {
      if (finished || killed) return;
      killed = true;
      child?.kill("SIGTERM");
      forceKill = setTimeout(() => { if (!finished) child?.kill("SIGKILL"); }, 5000);
    };
    /** 【CodingAgent】【输出收尾】@returns {void} 后代持有管道时依据最后输出时间结束等待 */
    const armIdle = () => {
      if (!exited || finished) return;
      clearTimeout(idle);
      idle = setTimeout(() => finish(exitCode), 100);
    };
    try {
      // 1. 【CodingAgent】【命令启动】隐藏 Windows 窗口，保留参数边界及会话目录
      child = spawn(command, args, { cwd: options.cwd ? path.resolve(cwd, options.cwd) : cwd, shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
      child.stdout.setEncoding("utf8"); child.stderr.setEncoding("utf8");
      child.stdout.on("data", data => { stdout += data; armIdle(); });
      child.stderr.on("data", data => { stderr += data; armIdle(); });
      child.once("error", () => finish(1));
      child.once("exit", code => { exited = true; exitCode = code ?? 0; armIdle(); });
      child.once("close", code => finish(code));
      // 2. 【CodingAgent】【命令等待】外部执行独立计时，不占用扩展 JavaScript 的计算时限
      if (options.signal?.aborted) kill();
      else options.signal?.addEventListener("abort", kill, { once: true });
      if (options.timeout > 0) deadline = setTimeout(kill, options.timeout);
    } catch { finish(1); }
  }).finally(() => {
    if (requestId) process.stdout.write(waitPrefix + JSON.stringify({ requestId, paused: false }) + "\n");
  });
}

function createUiContext(actions, payload) {
  const addUiAction = (method, fields = {}) => {
    recordAction({ type: "ui", method, ...fields }, payload.filePath);
  };
  return {
    /** 【CodingAgent】【选择对话】@param {string} title 标题 @param {string[]} options 选项 @param {object} dialogOptions 超时选项 @returns {Promise<string|undefined>} 选择结果 */
    select: async (title, options, dialogOptions = {}) => {
      if (payload.hasExtensionUi !== true) return undefined;
      const response = await requestUi({ method: "select", title: toText(title), options, timeout: dialogOptions.timeout }, dialogOptions.signal);
      return response?.cancelled ? undefined : response?.value;
    },
    /** 【CodingAgent】【确认对话】@param {string} title 标题 @param {string} message 内容 @param {object} dialogOptions 超时选项 @returns {Promise<boolean>} 确认结果 */
    confirm: async (title, message, dialogOptions = {}) => {
      if (payload.hasExtensionUi !== true) return false;
      const response = await requestUi({ method: "confirm", title: toText(title), message: toText(message), timeout: dialogOptions.timeout }, dialogOptions.signal);
      return response?.value === true;
    },
    /** 【CodingAgent】【输入对话】@param {string} title 标题 @param {string} placeholder 占位文本 @param {object} dialogOptions 超时选项 @returns {Promise<string|undefined>} 输入结果 */
    input: async (title, placeholder, dialogOptions = {}) => {
      if (payload.hasExtensionUi !== true) return undefined;
      const response = await requestUi({ method: "input", title: toText(title), placeholder, timeout: dialogOptions.timeout }, dialogOptions.signal);
      return response?.cancelled ? undefined : response?.value;
    },
    notify: (message, type) => addUiAction("notify", {
      message: toText(message),
      notifyType: typeof type === "string" ? type : undefined
    }),
    onTerminalInput: () => () => {},
    setStatus: (key, text) => addUiAction("setStatus", {
      statusKey: String(key ?? ""),
      statusText: text === undefined ? undefined : toText(text)
    }),
    setWorkingMessage: message => addUiAction("setWorkingMessage", {
      workingMessage: message === undefined ? undefined : toText(message)
    }),
    setWorkingIndicator: options => {
      const indicator = normalizeIndicator(options);
      addUiAction("setWorkingIndicator", {
        workingIndicatorFrames: indicator.frames,
        workingIndicatorIntervalMs: indicator.intervalMs
      });
    },
    setHiddenThinkingLabel: label => addUiAction("setHiddenThinkingLabel", {
      hiddenThinkingLabel: label === undefined ? undefined : toText(label)
    }),
    setWidget: (key, content, options = {}) => {
      const widgetLines = normalizeStringArray(content);
      if (content === undefined || widgetLines !== undefined) {
        addUiAction("setWidget", {
          widgetKey: String(key ?? ""),
          widgetLines,
          widgetPlacement: typeof options?.placement === "string" ? options.placement : undefined
        });
      }
    },
    setFooter: footer => addUiAction("setFooter", {
      footerLines: normalizeComponentLines(footer)
    }),
    setHeader: header => addUiAction("setHeader", {
      headerLines: normalizeComponentLines(header)
    }),
    setTitle: title => addUiAction("setTitle", { title: toText(title) }),
    custom: async () => undefined,
    pasteToEditor(text) { this.setEditorText(text); },
    setEditorText: text => addUiAction("set_editor_text", { text: toText(text) }),
    getEditorText: () => "",
    editor: async (title, prefill) => {
      if (payload.hasExtensionUi !== true) return undefined;
      const response = await requestUi({
        method: "editor",
        title: toText(title),
        prefill: prefill === undefined ? undefined : toText(prefill)
      });
      if (!response || response.cancelled === true) return undefined;
      return typeof response.value === "string" ? response.value : undefined;
    }
  };
}

/**
 * 【CodingAgent】【扩展读取】取得本次调用的会话快照，初始化阶段禁止使用运行时 API
 * @returns {object} 当前调用中的会话状态
 */
function readSession() {
  const context = requestContext.getStore();
  const payload = currentPayload();
  if ((context && activeRequests.has(context.id) && !context.sessionReady) || !payload?.session)
    throw new Error("Extension session is not initialized.");
  if (context?.payload?.session && livePayload?.session &&
      (context.payload.extensionGeneration !== livePayload.extensionGeneration || context.payload.session.header.id !== livePayload.session.header.id))
    throw new Error("Extension session is stale; use the withSession callback after replacing a session.");
  return payload.session;
}

/**
 * 【CodingAgent】【扩展写入】更新本次调用可见状态并立即按管道顺序通知宿主
 * @param {string} type 元数据条目类型
 * @param {object} fields 条目字段
 * @param {boolean} setup 是否为新会话初始化操作
 * @returns {string} 已追加条目标识
 */
function emitSessionEntry(type, fields, setup = false) {
  const session = readSession();
  const entry = { type, id: randomUUID().replaceAll("-", ""), parentId: session.leafId ?? null, timestamp: new Date().toISOString(), ...fields };
  // 1. 【CodingAgent】【扩展写入】先验证 JSON 可序列化，失败时不能留下半个内存操作
  const context = requestContext.getStore();
  const line = JSON.stringify({ requestId: context?.id ?? "", background: !context || !activeRequests.has(context.id), sessionId: session.header.id, entry, setup });
  const stored = JSON.parse(line).entry;
  session.entries.push(stored);
  session.leafId = stored.id;
  if (type === "session_info") session.name = stored.name;
  process.stdout.write(sessionActionPrefix + line + "\n");
  return stored.id;
}

/** 【CodingAgent】【只读快照】@param {*} value 待复制的 JSON 值 @returns {*} 不共享内部引用的副本 */
function cloneSessionValue(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

/**
 * 【CodingAgent】【分支读取】沿父链读取指定分支，不混入其他分支的自定义状态
 * @param {string|undefined} fromId 目标叶节点，默认当前叶节点
 * @returns {object[]} 从根到叶的条目副本
 */
function readSessionBranch(fromId) {
  const session = readSession();
  const byId = new Map(session.entries.map(entry => [entry.id, entry]));
  const branch = [], seen = new Set();
  let id = fromId ?? session.leafId;
  while (id && byId.has(id) && !seen.has(id)) {
    seen.add(id);
    const entry = byId.get(id);
    branch.unshift(entry);
    id = entry.parentId;
  }
  return cloneSessionValue(branch);
}

/** 【CodingAgent】【标签读取】@param {string} entryId 目标条目 @returns {string|undefined} 最新有效标签 */
function readSessionLabel(entryId) {
  const entries = readSession().entries;
  for (let i = entries.length - 1; i >= 0; i--) {
    if (entries[i].type === "label" && entries[i].targetId === entryId) return entries[i].label ?? undefined;
  }
  return undefined;
}

/** 【CodingAgent】【会话树读取】@returns {object[]} 包含条目、子节点和标签的完整树副本 */
function readSessionTree() {
  const entries = cloneSessionValue(readSession().entries);
  const nodes = new Map(entries.map(entry => [entry.id, { entry, children: [], label: readSessionLabel(entry.id) }]));
  const roots = [];
  for (const entry of entries) {
    const node = nodes.get(entry.id), parent = nodes.get(entry.parentId);
    if (parent) parent.children.push(node);
    else roots.push(node);
  }
  return roots;
}

/** 【CodingAgent】【会话上下文】@returns {object} 读取本次调用会话状态的只读接口 */
function createReadonlySessionManager() {
  return {
    /** @returns {string} 会话工作目录 */
    getCwd: () => readSession().header.cwd,
    /** @returns {string} 持久化目录，内存会话返回空字符串 */
    getSessionDir: () => readSession().sessionDir ?? "",
    /** @returns {string} 会话标识 */
    getSessionId: () => readSession().header.id,
    /** @returns {string|undefined} 持久化文件，内存会话为空 */
    getSessionFile: () => readSession().sessionFile ?? undefined,
    /** @returns {string|null} 当前叶节点 */
    getLeafId: () => readSession().leafId ?? null,
    /** @returns {object|undefined} 叶节点副本 */
    getLeafEntry: () => cloneSessionValue(readSession().entries.find(entry => entry.id === readSession().leafId)),
    /** @param {string} id 条目标识 @returns {object|undefined} 对应条目副本 */
    getEntry: id => cloneSessionValue(readSession().entries.find(entry => entry.id === id)),
    getLabel: readSessionLabel,
    getBranch: readSessionBranch,
    /** @returns {object} 会话头副本 */
    getHeader: () => cloneSessionValue(readSession().header),
    /** @returns {object[]} 所有条目的副本 */
    getEntries: () => cloneSessionValue(readSession().entries),
    getTree: readSessionTree,
    buildContextEntries: readContextEntries,
    buildSessionProjection: readSessionProjection,
    /** @returns {string|undefined} 当前名称 */
    getSessionName: () => readSession().name ?? undefined
  };
}

/** 【CodingAgent】【上下文条目】@returns {object[]} 经最新压缩规则筛选的来源条目 */
function readContextEntries() {
  const branch = readSessionBranch();
  const index = branch.findLastIndex(entry => entry.type === "compaction");
  if (index < 0) return branch;
  const compaction = branch[index];
  const start = branch.findIndex(entry => entry.id === compaction.firstKeptEntryId);
  const kept = start >= 0 && start < index ? branch.slice(start, index).filter(entry => !(entry.type === "message" && entry.message?.role === "system")) : [];
  return [compaction, ...kept, ...branch.slice(index + 1)];
}

/**
 * 【CodingAgent】【消息投影】把 Tau 存储字段转换为扩展消息字段
 * @param {object} stored 存储消息
 * @returns {object} 独立消息对象
 */
function projectStoredMessage(stored) {
  const message = cloneSessionValue(stored);
  if (message.timestamp && typeof message.timestamp === "string") message.timestamp = Date.parse(message.timestamp);
  if (message.content == null) message.content = message.role === "system" ? "" : [];
  if (message.role === "system" && Array.isArray(message.content)) message.content = message.content.map(block => block.text || "").join("\n");
  if (Array.isArray(message.content)) for (const block of message.content) {
    if (block.type === "toolCall" && typeof block.arguments === "string") {
      try { block.arguments = JSON.parse(block.arguments); } catch { }
    }
  }
  if (message.usage && Object.hasOwn(message.usage, "inputTokens")) {
    const usage = message.usage;
    message.usage = { ...usage, input: usage.inputTokens, output: usage.outputTokens, cacheRead: usage.cacheReadTokens ?? 0, cacheWrite: usage.cacheWriteTokens ?? 0,
      totalTokens: usage.totalTokens ?? (usage.inputTokens + usage.outputTokens + (usage.cacheReadTokens ?? 0) + (usage.cacheWriteTokens ?? 0)) };
  }
  if (typeof message.stopReason === "number") message.stopReason = ["stop", "length", "toolUse", "contentFilter", "error", "aborted", "deferred"][message.stopReason];
  return message;
}

/** 【CodingAgent】【会话投影】@returns {object} 保留来源和分支设置的模型上下文 */
function readSessionProjection() {
  const branch = readSessionBranch(), context = readContextEntries();
  let thinkingLevel = "off", model = null;
  for (const entry of branch) {
    if (entry.type === "thinking_level_change") thinkingLevel = entry.thinkingLevel;
    if ((entry.type === "model_change" || entry.type === "session_info") && entry.provider && (entry.modelId || entry.model)) model = { provider: entry.provider, modelId: entry.modelId || entry.model };
    if (entry.type === "message" && entry.message?.role === "assistant" && entry.message.provider && entry.message.model) model = { provider: entry.message.provider, modelId: entry.message.model };
  }
  const edits = new Map(context.filter(entry => entry.type === "context_edit").map(entry => [entry.targetId, entry]));
  const entries = context.map((sourceEntry, index) => {
    const entry = sourceEntry;
    let messages = [];
    if (entry.type === "message" && entry.message) messages = [projectStoredMessage(entry.message)];
    if (entry.type === "custom_message") messages = [{ role: "custom", customType: entry.customType, content: entry.content ?? [], display: entry.display, details: entry.details, timestamp: Date.parse(entry.timestamp) }];
    if (entry.type === "branch_summary" && entry.summary) messages = [{ role: "branchSummary", summary: entry.summary, fromId: entry.fromId, timestamp: Date.parse(entry.timestamp) }];
    if (entry.type === "compaction" && index === 0) {
      if (entry.systemMessage) messages.push(projectStoredMessage(entry.systemMessage));
      messages.push({ role: "compactionSummary", summary: entry.summary, tokensBefore: entry.tokensBefore, timestamp: Date.parse(entry.timestamp) });
    }
    if (edits.has(entry.id)) {
      const replacement = edits.get(entry.id).replacement;
      if (replacement == null) messages = [];
      else messages = messages.map(message => {
        if (!["user", "assistant", "toolResult", "custom"].includes(message.role)) return message;
        const content = ["assistant", "toolResult"].includes(message.role) && typeof replacement.content === "string" ? [{ type: "text", text: replacement.content }] : replacement.content;
        return { ...message, content: cloneSessionValue(content) };
      });
    }
    return { sourceEntry, messages };
  });
  return { entries, messages: entries.flatMap(entry => entry.messages), thinkingLevel, model };
}

/**
 * 【CodingAgent】【扩展上下文】组装命令、工具和事件共用的运行上下文
 * @param {object} api 扩展 API
 * @param {object} payload 调用参数
 * @param {object[]} actions 其他待处理操作
 * @returns {object} 扩展上下文
 */
function createCommandContext(api, payload, actions) {
  const context = {
    ui: createUiContext(actions, payload),
    hasUI: payload.hasExtensionUi === true,
    mode: typeof payload.extensionMode === "string" ? payload.extensionMode : (payload.hasExtensionUi === true ? "tui" : "print"),
    cwd: payload.cwd,
    /** 【CodingAgent】【项目信任】@returns {boolean} 当前项目是否允许加载本地资源 */
    isProjectTrusted: () => readRuntime().projectTrusted !== false,
    sessionManager: createReadonlySessionManager(),
    modelRegistry: createReadonlyModelRegistry(api),
    /** 【CodingAgent】【当前模型】@returns {object} 模型副本 */
    get model() { return cloneSessionValue(readRuntime().model); },
    /** 【CodingAgent】【范围快照】@returns {object[]} 当前会话限定的模型及其思考等级 */
    get scopedModels() { return cloneSessionValue(readRuntime().scopedModels ?? []); },
    /** 【CodingAgent】【思考等级】@returns {string|undefined} 当前模型的思考等级 */
    get thinkingLevel() { return readRuntime().thinkingLevel ?? undefined; },
    /** 【CodingAgent】【运行状态】@returns {boolean} 是否空闲 */
    isIdle: () => readRuntime().isIdle,
    /** 【CodingAgent】【请求取消】@returns {AbortSignal} 当前扩展调用的协作取消信号 */
    signal: requestContext.getStore().controller.signal,
    /** 【CodingAgent】【取消运行】@returns {void} 无返回值 */
    abort: () => emitRuntimeOperation("abort"),
    /** 【CodingAgent】【队列状态】@returns {boolean} 是否存在待处理消息 */
    hasPendingMessages: () => readRuntime().hasPendingMessages,
    /** 【CodingAgent】【优雅退出】@returns {void} 请求宿主完成当前操作后退出 */
    shutdown: () => emitRuntimeOperation("shutdown"),
    /** 【CodingAgent】【上下文用量】@returns {object|undefined} 用量估算 */
    getContextUsage: () => cloneSessionValue(readRuntime().contextUsage ?? undefined),
    /** 【CodingAgent】【扩展压缩】@param {object} options 摘要指令及完成、失败回调 @returns {void} 启动独立压缩操作 */
    compact: (options = {}) => {
      requestHost('compact', { customInstructions: options.customInstructions }, true)
        .then(result => options.onComplete?.(result))
        .catch(error => {
          try { options.onError?.(error instanceof Error ? error : new Error(String(error))); }
          catch (callbackError) { process.stderr.write(formatError(callbackError) + '\n'); }
        });
    },
    /** 【CodingAgent】【系统提示】@returns {string} 当前提示词 */
    getSystemPrompt: () => readRuntime().systemPrompt,
    /** 【CodingAgent】【基础提示】@returns {object} 独立于本轮临时覆盖的基础提示构建选项 */
    getSystemPromptOptions: () => cloneSessionValue(readRuntime().systemPromptOptions),
    /** 【CodingAgent】【等待空闲】@returns {Promise<void>} 运行结束任务 */
    waitForIdle: () => requestHost("waitForIdle"),
    /** 【CodingAgent】【新建会话】@param {object} options 父会话、初始化及新会话回调 @returns {Promise<object>} 实际取消结果 */
    newSession: (options = {}) => replaceExtensionSession("newSession", { parentSession: options.parentSession }, options),
    /** 【CodingAgent】【分叉会话】@param {string} entryId 分叉条目 @param {object} options 分叉位置及新会话回调 @returns {Promise<object>} 实际取消结果和选中文本 */
    fork: (entryId, options = {}) => replaceExtensionSession("fork", { entryId, position: options.position ?? "before" }, options),
    /** 【CodingAgent】【树导航】@param {string} targetId 目标条目 @param {object} options 摘要和标签选项 @returns {Promise<object>} 实际导航、摘要和编辑器文本结果 */
    navigateTree: (targetId, options = {}) => requestHost("navigateTree", { targetId, ...options }),
    /** 【CodingAgent】【恢复会话】@param {string} sessionPath 目标文件 @param {object} options 新会话回调 @returns {Promise<object>} 实际取消结果 */
    switchSession: (sessionPath, options = {}) => replaceExtensionSession("switchSession", { sessionPath }, options),
    /** 【CodingAgent】【重载扩展】@returns {Promise<void>} 新实例及资源准备完成后返回，原上下文随即失效 */
    reload: async () => {
      if (!["invoke", "invokeShortcut"].includes(requestContext.getStore().payload.mode)) throw new Error("Reload is only available in command contexts.");
      await requestHost("reload");
    },
    sendMessage: api.sendMessage,
    sendUserMessage: api.sendUserMessage
  };
  if (payload.mode === "executeTool") {
    let previousStarted = Promise.resolve();
    Object.defineProperty(context, "tools", { enumerable: true,
      /** 【CodingAgent】【嵌套目录】@returns {object[]} 当前允许调用的工具定义 */
      get: () => cloneSessionValue(readRuntime().tools.filter(tool => readRuntime().callableTools.includes(tool.name))) });
    /** 【CodingAgent】【嵌套执行】@param {string} name 工具名称 @param {*} args 参数 @param {object} options 取消及更新回调 @returns {Promise<object>} 完整调用结果 */
    context.executeTool = (name, args, options = {}) => {
      // 1. 【CodingAgent】【调用顺序】只等待上一请求登记，执行阶段仍可并行，避免线程池改变编号和顺序队列
      const previous = previousStarted;
      let started;
      previousStarted = new Promise(resolve => started = resolve);
      return previous.then(() => requestHost("executeNestedTool",
        { callerId: String(payload.toolCallId), name: String(name), args: args ?? {} }, false, { ...options, onStarted: started })).finally(started);
    };
  }
  return bindSessionContext(context, payload);
}

/**
 * 【CodingAgent】【上下文绑定】使保存的上下文与原会话及扩展实例绑定，防止闭包误读或误写新会话
 * @param {object} value 要绑定的上下文或只读管理器
 * @param {object} payload 创建时的请求快照
 * @returns {object} 校验实例和会话身份的代理
 */
function bindSessionContext(value, payload) {
  const generation = payload.extensionGeneration, sessionId = payload.session?.header.id;
  /** 【CodingAgent】【陈旧检查】@returns {void} 上下文已替换时抛出明确错误 */
  const assertCurrent = () => {
    if (livePayload && (livePayload.extensionGeneration !== generation || livePayload.session?.header.id !== sessionId))
      throw new Error("Extension session is stale; use the withSession callback after replacing a session.");
  };
  return new Proxy(value, {
    /** 【CodingAgent】【属性绑定】@param {object} target 原对象 @param {string|symbol} key 属性 @returns {*} 校验后的属性或函数 */
    get(target, key) {
      assertCurrent();
      const item = Reflect.get(target, key);
      if (typeof item === "function") return (...args) => { assertCurrent(); return item.apply(target, args); };
      if (["sessionManager", "modelRegistry", "ui"].includes(key)) return bindSessionContext(item, payload);
      return item;
    }
  });
}

/**
 * 【CodingAgent】【会话替换】执行宿主替换并把回调放入新实例的独立上下文
 * @param {string} operation 替换操作名称
 * @param {object} fields 序列化操作参数
 * @param {object} options 新会话初始化和完成回调
 * @returns {Promise<object>} 取消结果及选中文本
 */
async function replaceExtensionSession(operation, fields, options) {
  const parent = requestContext.getStore();
  if (!["invoke", "invokeShortcut"].includes(parent.payload.mode)) throw new Error("Session replacement is only available in command contexts.");
  const result = await requestHost(operation, fields);
  if (result.cancelled) return result;
  // 1. 【CodingAgent】【替换回调】旧命令仍可返回，但只有新上下文可以继续访问新会话
  await requestContext.run({ ...parent, payload: result.payload }, async () => {
    const { api } = await loadExtension(result.payload);
    if (options.setup) {
      try { await options.setup(createSetupSessionManager(result.payload)); }
      finally { await requestHost("refreshSessionContext"); }
    }
    if (options.withSession) {
      const fresh = createCommandContext(api, result.payload, parent.actions);
      /** 【CodingAgent】【完成等待】@param {Function} send 发送消息函数 @param {Array} args 消息参数 @returns {Promise<void>} 实际消息或回合完成任务 */
      const deliver = async (send, args) => {
        const actions = [];
        await requestContext.run({ ...requestContext.getStore(), actions }, async () => {
          send(...args);
          await requestHost("deliverReplacedMessage", { actions, filePath: result.payload.filePath });
        });
      };
      fresh.sendMessage = (...args) => deliver(api.sendMessage, args);
      fresh.sendUserMessage = (...args) => deliver(api.sendUserMessage, args);
      await options.withSession(fresh);
    }
  });
  return result.value;
}

/** 【CodingAgent】【新会话初始化】@param {object} payload 新会话请求快照 @returns {object} 可同步追加初始消息和元数据的会话管理器 */
function createSetupSessionManager(payload) {
  return bindSessionContext({
    ...createReadonlySessionManager(),
    /** @returns {boolean} 是否使用文件持久化 */
    isPersisted: () => Boolean(readSession().sessionFile),
    /** @param {object} message 初始消息 @returns {string} 消息条目标识 */
    appendMessage: message => emitSessionEntry("message", { message }, true),
    /** @param {string} customType 类型 @param {*} data 私有数据 @returns {string} 条目标识 */
    appendCustomEntry: (customType, data) => emitSessionEntry("custom", { customType, data }, true),
    /** @param {string} name 会话名称 @returns {string} 条目标识 */
    appendSessionInfo: name => emitSessionEntry("session_info", { name }, true),
    /** @param {string} thinkingLevel 思考等级 @returns {string} 条目标识 */
    appendThinkingLevelChange: thinkingLevel => emitSessionEntry("thinking_level_change", { thinkingLevel }, true),
    /** @param {string} provider 提供方 @param {string} modelId 模型标识 @returns {string} 条目标识 */
    appendModelChange: (provider, modelId) => emitSessionEntry("model_change", { provider, modelId }, true),
    /** @param {string} targetId 标签目标 @param {string|undefined} label 标签文本 @returns {string} 条目标识 */
    appendLabelChange: (targetId, label) => emitSessionEntry("label", { targetId, label }, true),
    /** @param {string} customType 类型 @param {*} content 内容 @param {boolean} display 是否显示 @param {*} details 私有详情 @returns {string} 条目标识 */
    appendCustomMessageEntry: (customType, content, display, details) => emitSessionEntry("custom_message", { customType, content, display, details }, true)
  }, payload);
}

/** 【CodingAgent】【模型目录】@param {object} api 当前扩展 API @returns {object} 基于宿主目录及认证状态的查询接口 */
function createReadonlyModelRegistry(api) {
  /** 【CodingAgent】【能力目录】@param {string} type 能力 @param {string|undefined} provider 提供方 @returns {object[]} 独立模型快照 */
  const modelsOfType = (type, provider) => cloneSessionValue((readRuntime().allModels ?? readRuntime().models)
    .filter(model => (model.type ?? "chat") === type && (provider === undefined || model.provider === provider)));
  /** 【CodingAgent】【认证协议】@param {object|undefined} value 宿主结果 @returns {object|undefined} 保持原生可选字段的 undefined 语义 */
  const normalizeAuth = value => value ? { auth: Object.fromEntries(Object.entries(value.auth ?? {}).filter(([, field]) => field !== null)),
    ...(value.env == null ? {} : { env: value.env }), ...(value.source == null ? {} : { source: value.source }) } : undefined;
  return {
    /** @returns {string|undefined} 当前目录加载及刷新错误 */
    getError: () => readRuntime().modelRegistryError,
    /** @returns {object[]} 全部模型副本 */
    getAll: () => cloneSessionValue(readRuntime().models),
    /** @returns {object[]} 具有认证的模型副本 */
    getAvailable: () => {
      const runtime = readRuntime();
      return runtime.availableProviders.flatMap(provider => {
        const models = cloneSessionValue(runtime.models.filter(model => model.provider === provider));
        const entry = extensionProviders.get(provider);
        const physical = models.filter(model => model.api !== "pi-virtual");
        // 1. 【CodingAgent】【账户权限】原生扩展拥有自己的过滤规则；内置权限只携带 ID，不包含认证令牌
        const ids = runtime.builtInModelPermissions?.[provider];
        const available = entry?.config.canonical ? entry.config.canonical.filterModels?.(physical, entry.credential) ?? physical
          : Array.isArray(ids) ? physical.filter(model => ids.includes(model.id)) : physical;
        return [...available, ...models.filter(model => model.api === "pi-virtual")];
      });
    },
    /** @param {string} provider 提供方 @param {string} modelId 模型标识 @returns {object|undefined} 匹配模型 */
    find: (provider, modelId) => cloneSessionValue(readRuntime().models.find(model => model.provider === provider && model.id === modelId)),
    /** @param {string} type 能力 @param {string|undefined} provider 提供方 @returns {object[]} 指定能力模型 */
    getModelsOfType: modelsOfType,
    /** @param {string} type 能力 @param {string|undefined} provider 提供方 @param {object} options 取消选项 @returns {Promise<object[]>} 认证检查及过滤后的模型 */
    getAvailableOfType: (type, provider, options = {}) => requestHost("modelRegistryAvailable", { type, provider }, false, { signal: options.signal }),
    /** @param {string} type 能力 @param {string} provider 提供方 @param {string} modelId 标识 @returns {object|undefined} 指定能力模型 */
    getModelOfType: (type, provider, modelId) => modelsOfType(type, provider).find(model => model.id === modelId),
    /** @param {string} type 能力 @param {string} provider 提供方 @param {string} modelId 标识 @returns {object|undefined} 指定能力模型 */
    findOfType: (type, provider, modelId) => modelsOfType(type, provider).find(model => model.id === modelId),
    /** @param {object} model 目标模型 @returns {Promise<string|undefined>} 实际认证密钥 */
    getApiKey: model => requestHost("getApiKey", { provider: model.provider, modelId: model.id, modelType: model.type ?? "chat" }),
    /** @param {object} model 模型 @param {object} context 独立上下文 @param {object} options 协议选项 @returns {object} 原生助手事件流 */
    stream: (model, context, options = {}) => createRegistryStream(model, context, options, false),
    /** @param {object} model 模型 @param {object} context 独立上下文 @param {object} options 通用选项 @returns {object} 原生助手事件流 */
    streamSimple: (model, context, options = {}) => createRegistryStream(model, context, options, true),
    /** @param {object} model 模型 @param {object} context 独立上下文 @param {object} options 协议选项 @returns {Promise<object>} 助手终值 */
    complete: (model, context, options = {}) => createRegistryStream(model, context, options, false).result(),
    /** @param {object} model 图像模型 @param {object} context 图像输入 @param {object} options 请求选项 @returns {Promise<object>} 图像结果或错误终值 */
    generateImages: (model, context, options = {}) => callRegistryCapability("image", model, context, options),
    /** @param {object} model 分类模型 @param {object} context 结构化输入 @param {object} options 请求选项 @returns {Promise<object>} 分类结果或错误终值 */
    classify: (model, context, options = {}) => callRegistryCapability("classifier", model, context, options),
    /** @param {string} provider 提供方 @returns {Promise<object|undefined>} 实际请求认证 */
    getProviderAuth: async provider => normalizeAuth(await requestHost("getProviderAuth", { provider })),
    /** @param {string} provider 提供方 @returns {Promise<string|undefined>} 密钥，解析失败时为空 */
    getApiKeyForProvider: async provider => { try { return (await requestHost("getProviderAuth", { provider }))?.auth?.apiKey ?? undefined; } catch { return undefined; } },
    /** @param {object} model 模型 @returns {Promise<object>} 完整认证或明确错误 */
    getApiKeyAndHeaders: async model => {
      try { const value = normalizeAuth(await requestHost("getProviderAuth", { provider: model.provider, modelId: model.id, modelType: model.type ?? "chat" })); return { ok: true, ...value?.auth, env: value?.env }; }
      catch (error) { return { ok: false, error: error.message }; }
    },
    /** @param {string} provider 提供方 @returns {object} 不包含秘密的配置状态 */
    getProviderAuthStatus: provider => {
      const { usesOAuth, ...status } = readRuntime().providerAuth?.[provider] ?? { configured: false };
      return cloneSessionValue(status);
    },
    /** @param {object} model 模型 @returns {boolean} 是否使用存储的 OAuth */
    isUsingOAuth: model => readRuntime().providerAuth?.[model.provider]?.usesOAuth === true,
    /** @param {string} provider 提供方 @returns {string} 显示名称 */
    getProviderDisplayName: provider => { const runtime = readRuntime(); const config = extensionProviders.get(provider)?.config;
      return config?.name ?? runtime.providerDisplayNames?.[provider] ?? config?.oauth?.name ?? provider; },
    /** @param {string|object} provider 提供方或原生实例 @param {object} config 配置 @returns {void} 注册提供方 */
    registerProvider: (provider, config) => api.registerProvider(provider, config),
    /** @param {string} provider 提供方 @returns {void} 移除注册 */
    unregisterProvider: provider => api.unregisterProvider(provider),
    /** @param {object} definition 虚拟模型定义 @returns {void} 注册路由 */
    registerVirtualModel: definition => api.registerVirtualModel(definition),
    /** @param {string} provider 提供方 @param {string} id 模型标识 @returns {void} 注销路由 */
    unregisterVirtualModel: (provider, id) => api.unregisterVirtualModel(provider, id),
    /** @returns {string[]} 当前显式提供方注册标识 */
    getRegisteredProviderIds: () => { readRuntime(); return [...extensionProviders.keys()]; },
    /** @param {string} provider 提供方 @returns {object|undefined} 原始扩展配置及回调 */
    getRegisteredProviderConfig: provider => { readRuntime(); const entry = extensionProviders.get(provider); return entry?.config.canonical ? undefined : entry?.registeredConfig ?? entry?.config; },
    /** @param {string} provider 提供方 @returns {object|undefined} 原生实例 */
    getRegisteredNativeProvider: provider => { readRuntime(); return extensionProviders.get(provider)?.config.canonical; },
    /** @param {string} provider 提供方 @returns {boolean} 是否已配置认证 */
    hasConfiguredAuth: provider => readRuntime().availableProviders.includes(typeof provider === "string" ? provider : provider.provider),
    /** @param {object} options 网络、范围及取消选项 @returns {Promise<object>} 刷新状态和逐提供方错误 Map */
    refresh: async (options = {}) => {
      const result = await requestHost("refresh", { allowNetwork: options.allowNetwork, force: options.force, providers: options.providers }, false, { signal: options.signal });
      return { aborted: result?.aborted === true, errors: new Map(Object.entries(result?.errors ?? {}).map(([provider, message]) => [provider, new Error(message)])) };
    }
  };
}

/** 【CodingAgent】【目录流式】@param {object} model 模型 @param {object} context 上下文 @param {object} options 参数及回调 @param {boolean} simple 简化模式 @returns {object} 可立即消费的原生事件流 */
function createRegistryStream(model, context, options, simple) {
  readRuntime();
  const stream = new RegistryAssistantMessageStream();
  const callId = randomUUID();
  const callbackContext = requestContext.getStore();
  const { signal, onPayload, onResponse, onProviderStreamEvent, transformHeaders, ...settings } = options;
  modelRegistryCallbacks.set(callId, { context: callbackContext, options });
  let last;
  let ended = false;
  /** 【CodingAgent】【流式失败】@param {Error} error 失败原因 @returns {void} 产生可正常消费的终止帧 */
  const fail = error => {
    if (ended) return;
    ended = true;
    const message = { ...(last ?? { role: "assistant", content: [], api: model.api, provider: model.provider, model: model.id,
      timestamp: Date.now(), usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } }),
      stopReason: signal?.aborted ? "aborted" : "error", errorMessage: error.message };
    stream.push({ type: "error", reason: message.stopReason, error: message });
  };
  void requestHost("modelRegistryStream", { provider: model.provider, modelId: model.id, filePath: currentPayload().filePath,
    callId, context, options: settings, simple, callbacks: { payload: typeof onPayload === "function", response: typeof onResponse === "function",
      streamEvent: typeof onProviderStreamEvent === "function", headers: typeof transformHeaders === "function" } }, false,
    { signal, onUpdate: event => { if (!ended) { last = event.partial ?? event.message ?? event.error; ended = event.type === "done" || event.type === "error"; stream.push(event); } } })
    .then(() => { if (!ended) fail(new Error("Model stream ended without a terminal event.")); }, fail)
    .finally(() => modelRegistryCallbacks.delete(callId));
  return stream;
}

/** 【CodingAgent】【能力调用】@param {string} type 能力 @param {object} model 模型 @param {object} context 输入 @param {object} options 选项 @returns {Promise<object>} 非拒绝式结果 */
async function callRegistryCapability(type, model, context, options) {
  const callId = randomUUID();
  const { signal, onPayload, onResponse, ...settings } = options;
  modelRegistryCallbacks.set(callId, { context: requestContext.getStore(), options });
  try {
    if ((model.type ?? "chat") !== type) throw new Error(`Model '${model.provider}/${model.id}' is not a ${type} model.`);
    return await requestHost("modelRegistryCapability", { type, provider: model.provider, modelId: model.id, filePath: currentPayload().filePath,
      callId, context, options: settings, callbacks: { payload: typeof onPayload === "function", response: typeof onResponse === "function" } }, false, { signal });
  } catch (error) {
    return { api: model.api, provider: model.provider, model: model.id, ...(type === "image" ? { output: [] } : { answers: {} }),
      stopReason: signal?.aborted ? "aborted" : "error", errorMessage: error.message, timestamp: Date.now() };
  } finally { modelRegistryCallbacks.delete(callId); }
}

async function loadFactory(filePath) {
  installExtensionImportHook(isTypeScriptFile(filePath));
  const url = pathToFileURL(filePath).href + "?tauCacheBust=" + Date.now() + "-" + Math.random();
  const module = await import(url);
  let factory = module.default;
  if (factory && typeof factory !== "function" && typeof factory.default === "function") {
    factory = factory.default;
  }
  if (typeof factory !== "function") {
    throw new Error("Extension does not export a valid factory function");
  }
  return factory;
}

async function renderCustomMessageActions(actions, messageRendererMap) {
  for (const action of actions) {
    if (!action || action.type !== "customMessage" || action.display === false) continue;
    const key = String(action.customType ?? "");
    const renderer = messageRendererMap.get(key);
    if (typeof renderer !== "function") continue;
    try {
      const message = {
        role: "custom",
        customType: key,
        content: Object.prototype.hasOwnProperty.call(action, "content") ? action.content : "",
        display: action.display !== false,
        details: Object.prototype.hasOwnProperty.call(action, "details") ? action.details : undefined,
        timestamp: typeof action.timestamp === "number" ? action.timestamp : Date.now()
      };
      const rendered = await renderer(message, { expanded: true }, themeProxy);
      const lines = normalizeComponentLines(rendered) ?? [];
      if (lines.length > 0) action.renderedLines = lines;
    } catch {
    }
  }
}

/**
 * 【CodingAgent】【扩展生命周期】每个扩展仅执行一次工厂，并共享并发初始化结果
 * @param {object} payload 包含扩展路径的请求
 * @returns {Promise<object>} 扩展注册表及 API
 */
function loadExtension(payload) {
  const key = path.resolve(payload.filePath);
  if (!extensions.has(key)) {
    const initialization = initializeExtension(payload);
    extensions.set(key, initialization);
    initialization.catch(() => extensions.delete(key));
  }
  return extensions.get(key);
}

/**
 * 【CodingAgent】【扩展生命周期】创建注册表并执行扩展工厂
 * @param {object} payload 初始化参数
 * @returns {Promise<object>} 已初始化的扩展状态
 */
async function initializeExtension(payload) {
  const commandMap = new Map();
  const toolMap = new Map();
  const flagMap = new Map();
  const shortcutMap = new Map();
  const flagValues = new Map();
  const handlerMap = new Map();
  const messageRendererMap = new Map();
  const entryRendererMap = new Map();
  const markdownRegistration = {};
  const unsupported = { tools: 0, flags: 0, shortcuts: 0, handlers: 0, messageRenderers: 0, providers: 0 };
  const api = createApi(commandMap, toolMap, flagMap, shortcutMap, flagValues, handlerMap, messageRendererMap, entryRendererMap, markdownRegistration, unsupported, payload);
  const factory = await loadFactory(payload.filePath);
  const transaction = [];
  requestContext.getStore().providerTransaction = transaction;
  try { await factory(api); }
  catch (error) {
    // 1. 【CodingAgent】【注册回滚】按版本倒序撤销注册和注销，不覆盖其他工厂已经发布的结果
    for (const change of transaction.reverse()) if (providerVersions.get(change.versionKey ?? change.id) === change.version) {
      const target = change.mcp ? mcpServers : change.virtual ? virtualModels : extensionProviders;
      if (change.previous) target.set(change.id, change.previous); else target.delete(change.id);
      providerVersions.set(change.versionKey ?? change.id, change.previousVersion);
    }
    throw error;
  }
  finally { delete requestContext.getStore().providerTransaction; }
  const state = { commandMap, toolMap, flagMap, shortcutMap, handlerMap, messageRendererMap, entryRendererMap, markdownRegistration, unsupported, api };
  readyExtensions.set(path.resolve(payload.filePath), state);
  return state;
}

/** 【CodingAgent】【工具组合】@param {object} value 原始工具和名称集合 @returns {object} 提供访问策略与命名组查询的组合 */
function createToolLoadout(value) {
  const registered = cloneSessionValue(value.registered ?? []);
  const byName = new Map(registered.map(tool => [tool.name, tool]));
  return {
    registered,
    declared: (value.declared ?? []).map(name => byName.get(name)).filter(Boolean),
    callable: (value.callable ?? []).map(name => byName.get(name)).filter(Boolean),
    getExposure: name => byName.get(name)?.exposure ?? "direct",
    getNamespace: name => byName.get(name)?.namespace ?? undefined
  };
}

/** 【CodingAgent】【默认工具】@param {object|undefined} tool 原始定义 @returns {boolean} 是否具有注册时默认启用资格 */
function isToolActiveOnRegistration(tool) {
  return !!tool && ["direct", "model-only"].includes(tool.exposure ?? "direct") && tool.defaultActive !== false;
}

/** 【CodingAgent】【工具组合】@param {object} runtime 当前可变运行时快照 @returns {object} 说明替换、隐藏声明及错误列表 */
function prepareRuntimeToolLoadout(runtime) {
  const known = new Set(runtime.tools.filter(tool => tool.exposure !== "hidden").map(tool => tool.name));
  runtime.activeTools = [...new Set(runtime.activeTools)].filter(name => known.has(name));
  runtime.callableTools = runtime.tools.filter(tool => ["codemode", "deferred"].includes(tool.exposure) ||
    (tool.exposure ?? "direct") === "direct" && runtime.activeTools.includes(tool.name)).map(tool => tool.name);
  const loadout = createToolLoadout({ registered: runtime.tools, declared: runtime.activeTools, callable: runtime.callableTools });
  const changes = { descriptions: {}, hiddenDeclarations: [], errors: [] };
  // 1. 【CodingAgent】【工具组合】本进程同步执行准备钩子，宿主应用结果时不再反向等待 Node
  for (const tool of loadout.declared) {
    const definition = tool.extensionPath ? readyExtensions.get(path.resolve(tool.extensionPath))?.toolMap.get(tool.name) : undefined;
    try {
      const update = definition?.prepareLoadout?.(loadout);
      Object.assign(changes.descriptions, update?.descriptions);
      changes.hiddenDeclarations.push(...(update?.hiddenDeclarations ?? []));
    } catch (error) { changes.errors.push({ filePath: tool.extensionPath, error: String(error?.message ?? error) }); }
  }
  return changes;
}

/** 【CodingAgent】【工具协议】@param {object} tool 原始工具定义 @returns {object} 不含执行函数的完整工具元数据 */
function serializeToolDefinition(tool) {
  return {
    name: String(tool?.name ?? ""), label: typeof tool?.label === "string" ? tool.label : String(tool?.name ?? ""),
    description: typeof tool?.description === "string" ? tool.description : "",
    parameters: tool?.parameters && typeof tool.parameters === "object" ? tool.parameters : { type: "object" },
    hasHandler: typeof tool?.execute === "function", hasPrepareArguments: typeof tool?.prepareArguments === "function",
    hasPrepareLoadout: typeof tool?.prepareLoadout === "function", outputSchema: tool?.outputSchema,
    constrainedSampling: tool?.constrainedSampling, exposure: tool?.exposure ?? "direct", defaultActive: tool?.defaultActive,
    namespace: tool?.namespace, annotations: tool?.annotations, promptSnippet: tool?.promptSnippet,
    promptGuidelines: Array.isArray(tool?.promptGuidelines) ? tool.promptGuidelines : [], executionMode: tool?.executionMode
  };
}

/** 【CodingAgent】【原生认证】@param {object} oauth 原生 OAuth 实现 @returns {object} 保留交互和完整 ModelAuth 的桥接适配 */
function adaptCanonicalOAuth(oauth) {
  return { name: oauth.name, isSubscription: oauth.isSubscription, loginLabel: oauth.loginLabel,
    login: oauth.login?.bind(oauth), refreshToken: oauth.refresh?.bind(oauth), toAuth: oauth.toAuth?.bind(oauth),
    getApiKey: async credential => (await oauth.toAuth(credential)).apiKey };
}

/** 【CodingAgent】【提供方协议】@returns {object[]} 配置及回调能力，不传输 JavaScript 函数 */
function serializeProviders() {
  const providers = [...extensionProviders.values()].map(({ id, config, filePath, apiKeyAuthStatus }) => ({ id, filePath, version: providerVersions.get(id),
    config: { name: config.name, api: config.api, apiKey: config.apiKey, baseUrl: config.baseUrl,
      models: config.models, headers: config.headers, authHeader: config.authHeader },
    oauth: config.oauth ? { name: config.oauth.name, usesCallbackServer: config.oauth.usesCallbackServer, isSubscription: config.oauth.isSubscription, loginLabel: config.oauth.loginLabel } : undefined,
    apiKeyAuth: config.canonical?.auth?.apiKey ? { name: config.canonical.auth.apiKey.name, hasLogin: typeof config.canonical.auth.apiKey.login === "function",
      hasResolve: typeof config.canonical.auth.apiKey.resolve === "function",
      configured: apiKeyAuthStatus?.type === "api_key", source: apiKeyAuthStatus?.source } : undefined,
    isNative: !!config.canonical, hasStreamSimple: typeof config.streamSimple === "function", hasStream: typeof config.stream === "function",
    hasGenerateImages: typeof config.canonical?.generateImages === "function", hasClassify: typeof config.canonical?.classify === "function",
    hasRefreshModels: typeof config.refreshModels === "function" || typeof config.oauth?.modifyModels === "function" || !!config.canonical }));
  for (const entry of virtualModels.values()) {
    let provider = providers.find(provider => provider.id === entry.model.provider);
    if (!provider) { provider = { id: entry.model.provider, config: {}, isVirtualOnly: true, filePath: entry.filePath }; providers.push(provider); }
    (provider.virtualModels ??= []).push({ model: entry.model, filePath: entry.filePath, version: entry.version });
  }
  return providers;
}

/** 【CodingAgent】【MCP 校验】@param {string} name 服务器名 @param {object} raw 配置 @returns {object} 规范化的独立配置 */
function validateMcpServerConfig(name, raw) {
  /** 【CodingAgent】【MCP 类型】@param {*} value 配置值 @returns {boolean} 是否为普通对象 */
  const record = value => value !== null && typeof value === "object" && !Array.isArray(value);
  /** 【CodingAgent】【MCP 错误】@param {string} reason 原因 @returns {never} 抛出校验错误 */
  const fail = reason => { throw new Error(`server "${name}": ${reason}`); };
  /** 【CodingAgent】【MCP 地址】@param {*} value 地址 @returns {URL|undefined} 解析结果 */
  const address = value => typeof value === "string" && URL.canParse(value) ? new URL(value) : undefined;
  const loopback = ["localhost", "127.0.0.1", "[::1]"];
  if (typeof name !== "string" || !/^[A-Za-z0-9_-]+$/.test(name)) fail('invalid server name (use letters, digits, "_" and "-")');
  if (!record(raw)) fail("must be an object");
  const value = structuredClone(raw), exposures = ["codemode", "deferred", "direct", "hidden"];
  /** 【CodingAgent】【MCP 策略】@param {*} item 原始策略 @returns {string} 规范策略 */
  const exposure = item => {
    if (item === "codemode-deferred") item = "codemode";
    if (!exposures.includes(item)) fail("exposure must be one of " + exposures.join(", "));
    return item;
  };
  if (value.exposure !== undefined) value.exposure = exposure(value.exposure);
  if (value.toolExposure !== undefined) {
    if (!record(value.toolExposure)) fail("toolExposure must map tool names to exposures");
    for (const key of Object.keys(value.toolExposure)) value.toolExposure[key] = exposure(value.toolExposure[key]);
  }
  if (value.enabled !== undefined && typeof value.enabled !== "boolean") fail("enabled must be a boolean");
  if (value.description !== undefined && typeof value.description !== "string") fail("description must be a string");
  if (value.timeout !== undefined && (typeof value.timeout !== "number" || !(value.timeout > 0))) fail("timeout must be a positive number of seconds");
  if (value.type === "sse") fail("legacy SSE transport is not supported; use the streamable HTTP URL");
  /** 【CodingAgent】【MCP 映射】@param {*} map 字典 @param {string} field 字段 @returns {void} 校验字符串映射 */
  const stringMap = (map, field) => { if (map !== undefined && (!record(map) || Object.values(map).some(item => typeof item !== "string"))) fail(field + " must map names to strings"); };
  if (typeof value.url === "string" && [undefined, "http", "streamable-http"].includes(value.type)) {
    const url = address(value.url);
    if (!url || !["http:", "https:"].includes(url.protocol)) fail("url must be an http or https URL");
    stringMap(value.headers, "headers");
    if (value.auth !== undefined) {
      if (!record(value.auth) || typeof value.auth.provider !== "string" || !value.auth.provider) fail("auth.provider must be a provider name");
      if (url.protocol !== "https:" && !loopback.includes(url.hostname)) fail("auth requires an https URL or loopback HTTP");
    }
    if (value.oauth !== undefined) {
      const oauth = value.oauth;
      if (!record(oauth)) fail("oauth must be an object");
      for (const key of ["clientId", "clientSecret", "scope"]) if (oauth[key] !== undefined && typeof oauth[key] !== "string") fail("oauth." + key + " must be a string");
      const port = oauth.callbackPort, callback = address(oauth.callbackUrl);
      if (port !== undefined && (!Number.isInteger(port) || port < 1 || port > 65535)) fail("oauth.callbackPort must be a port number");
      if (oauth.callbackUrl !== undefined && (!callback || callback.protocol !== "http:" || !loopback.includes(callback.hostname) || callback.search || callback.hash)) fail("oauth.callbackUrl must be a loopback http URI without query or fragment");
      if (callback?.port && port !== undefined && Number(callback.port) !== port) fail("oauth.callbackUrl and oauth.callbackPort name different ports");
      if (oauth.clientName !== undefined && (typeof oauth.clientName !== "string" || !oauth.clientName.trim())) fail("oauth.clientName must be a non-empty string");
      if (oauth.clientRegistration !== undefined && oauth.clientRegistration !== "dcr") {
        if (oauth.clientRegistration !== "cimd") fail('oauth.clientRegistration must be "dcr" or "cimd"');
        if (oauth.clientId !== undefined || oauth.clientName !== undefined) fail('oauth.clientRegistration "cimd" cannot be combined with oauth.clientId or oauth.clientName');
        if (callback && (callback.hostname === "[::1]" || callback.pathname !== "/callback")) fail('oauth.clientRegistration "cimd" requires callback path /callback on localhost or 127.0.0.1');
      }
      const metadata = address(oauth.authServerMetadataUrl);
      if (oauth.authServerMetadataUrl !== undefined && (!metadata || !(metadata.protocol === "https:" || metadata.protocol === "http:" && loopback.includes(metadata.hostname)))) fail("oauth.authServerMetadataUrl must be https or loopback http");
    }
    return value;
  }
  if (typeof value.command === "string" && [undefined, "stdio"].includes(value.type)) {
    if (value.args !== undefined && (!Array.isArray(value.args) || value.args.some(item => typeof item !== "string"))) fail("args must be an array of strings");
    stringMap(value.env, "env");
    if (value.cwd !== undefined && typeof value.cwd !== "string") fail("cwd must be a string");
    return value;
  }
  fail('needs either "command" (stdio) or "url" (streamable HTTP)');
}

/** 【CodingAgent】【MCP 注册】@param {string} name 名称 @param {object} raw 配置 @param {string} extensionPath 所属扩展 @returns {void} 校验归属和命名空间 */
function registerMcpServer(name, raw, extensionPath) {
  const config = validateMcpServerConfig(name, raw), owner = mcpServers.get(name)?.extensionPath;
  if (owner !== undefined && owner !== extensionPath) throw new Error(`MCP server "${name}" is already registered by extension "${owner}"`);
  const clash = [...mcpServers.keys()].find(other => other !== name && other.replaceAll("-", "_") === name.replaceAll("-", "_"));
  if (clash) throw new Error(`MCP server "${name}" conflicts with registered server "${clash}"`);
  mutateMcpServer(name, { name, config, extensionPath });
}

/** 【CodingAgent】【MCP 事务】@param {string} name 名称 @param {object|undefined} server 新定义 @returns {void} 记录回滚版本并通知宿主 */
function mutateMcpServer(name, server) {
  const version = ++providerVersion, versionKey = "mcp\0" + name;
  requestContext.getStore()?.providerTransaction?.push({ id: name, mcp: true, previous: mcpServers.get(name), previousVersion: providerVersions.get(versionKey), versionKey, version });
  providerVersions.set(versionKey, version);
  if (server) mcpServers.set(name, server); else mcpServers.delete(name);
  if (requestContext.getStore()?.sessionReady) emitRuntimeOperation("registerMcpServers", { servers: [...mcpServers.values()] });
}

/** 【CodingAgent】【虚拟目录】@param {object} runtime 当前快照 @param {object|undefined} providerChange 尚未提交的提供方注册变更 @returns {void} 将虚拟条目叠加到物理目录 */
function updateVirtualSnapshot(runtime, providerChange) {
  runtime.physicalModels ??= runtime.allModels ?? runtime.models;
  const overlays = [...virtualModels.values()].map(entry => entry.model);
  runtime.allModels = runtime.physicalModels.filter(model => model.type && model.type !== "chat" ||
    !overlays.some(overlay => overlay.provider === model.provider && overlay.id === model.id)).concat(cloneSessionValue(overlays));
  runtime.models = runtime.allModels.filter(model => !model.type || model.type === "chat");
  // 1. 【CodingAgent】【虚拟认证归属】移除旧路由生成的免密状态，再根据当前目录重新生成
  const previous = new Set(runtime.virtualAvailableProviders ?? []);
  runtime.availableProviders = runtime.availableProviders.filter(provider => !previous.has(provider));
  runtime.virtualAvailableProviders = [];
  for (const model of overlays)
    if (!(model.provider === providerChange?.id ? providerChange.registered : extensionProviders.has(model.provider)) &&
        !runtime.physicalModels.some(item => item.provider === model.provider)) {
      if (!runtime.availableProviders.includes(model.provider)) runtime.availableProviders.push(model.provider);
      if (!runtime.virtualAvailableProviders.includes(model.provider)) runtime.virtualAvailableProviders.push(model.provider);
    }
}

/** 【CodingAgent】【虚拟注册】@param {object} definition 模型定义 @param {string} filePath 扩展路径 @returns {void} 校验并原子替换路由 */
function registerVirtualModel(definition, filePath) {
  const { provider, id } = definition;
  if (typeof provider !== "string" || !provider.trim() || typeof id !== "string" || !id.trim()) throw new Error("Virtual model provider and id must not be empty.");
  if (typeof definition.route !== "function") throw new Error("Virtual model route must be a function.");
  const key = provider + "\0" + id;
  const runtime = requestContext.getStore()?.sessionReady ? readRuntime() : undefined;
  const physical = runtime ? (runtime.physicalModels ?? runtime.allModels ?? runtime.models).filter(model => model.provider === provider)
    : extensionProviders.get(provider)?.config.models ?? currentPayload().providerDefaults?.[provider] ?? [];
  if (!virtualModels.has(key) && physical.some(model => model.id === id && (!model.type || model.type === "chat") && model.api !== "pi-virtual"))
    throw new Error(`Virtual model ${provider}/${id} conflicts with a physical model.`);
  const levels = definition.thinkingLevels ?? ["off"];
  const model = { id, provider, name: definition.name, api: "pi-virtual", baseUrl: "", reasoning: levels.some(level => level !== "off"),
    thinkingLevelMap: Object.fromEntries(["off","minimal","low","medium","high","xhigh","max"].map(level => [level, levels.includes(level) ? level : null])),
    input: definition.input ?? ["text","image"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: definition.contextWindow ?? 0, maxTokens: definition.maxTokens ?? 0 };
  const version = ++providerVersion;
  requestContext.getStore()?.providerTransaction?.push({ id: key, virtual: true, previous: virtualModels.get(key), previousVersion: providerVersions.get(key), version });
  providerVersions.set(key, version);
  virtualModels.set(key, { model, route: definition.route, filePath, version });
  if (runtime) { updateVirtualSnapshot(runtime); emitRuntimeOperation("registerProviders", { providers: serializeProviders() }); }
}

/** 【CodingAgent】【虚拟注销】@param {string} provider 提供方 @param {string} id 模型标识 @returns {void} 恢复被虚拟条目遮盖的物理目录 */
function unregisterVirtualModel(provider, id) {
  const key = provider + "\0" + id;
  if (!virtualModels.has(key)) return;
  const version = ++providerVersion;
  requestContext.getStore()?.providerTransaction?.push({ id: key, virtual: true, previous: virtualModels.get(key), previousVersion: providerVersions.get(key), version });
  providerVersions.set(key, version); virtualModels.delete(key);
  if (requestContext.getStore()?.sessionReady) { updateVirtualSnapshot(readRuntime()); emitRuntimeOperation("registerProviders", { providers: serializeProviders() }); }
}

/** 【CodingAgent】【认证上下文】@param {object|undefined} environment 显式环境覆盖 @returns {object} 原生异步环境和文件存在性接口 */
function createProviderAuthContext(environment) {
  return { env: async name => environment?.[name] ?? process.env[name], fileExists: async file => {
    const resolved = file === "~" ? require("node:os").homedir() : /^~[/\\]/.test(file) ? path.join(require("node:os").homedir(), file.slice(2)) : file;
    try { await fs.promises.access(resolved); return true; } catch { return false; }
  }};
}

/** 【CodingAgent】【注册事务】@param {string} id 提供方 @param {object|undefined} entry 新注册或删除 @returns {void} 修改并记录工厂回滚信息 */
function mutateProviderRegistration(id, entry) {
  const version = ++providerVersion;
  requestContext.getStore()?.providerTransaction?.push({ id, previous: extensionProviders.get(id), previousVersion: providerVersions.get(id), version });
  providerVersions.set(id, version);
  if (entry) extensionProviders.set(id, entry); else extensionProviders.delete(id);
}

/** 【CodingAgent】【提供方校验】@param {string} id 提供方 @param {object} config 配置 @param {object[]} baseline 原模型 @returns {void} 注册前拒绝无法执行的模型定义 */
function validateProviderConfig(id, config, baseline) {
  if (config.oauth) {
    if (!config.canonical && !config.baseUrl) throw new Error(`Provider '${id}': baseUrl is required when oauth is set.`);
    for (const method of ["login", "refreshToken", "getApiKey"])
      if (typeof config.oauth[method] !== "function") throw new Error(`Provider '${id}': oauth.${method} must be a function.`);
  }
  for (const model of config.models ?? []) {
    if (!model || typeof model.id !== "string" || !model.id.length) throw new Error(`Provider '${id}': each model requires an id.`);
    validateModelMetadata(model);
    if (!["chat", "image", "classifier"].includes(model.type ?? "chat")) throw new Error(`Provider '${id}', model '${model.id}': unknown model type '${model.type}'.`);
    const candidates = baseline.filter(item => (item.type ?? "chat") === (model.type ?? "chat"));
    const defaults = candidates.find(item => item.id === model.id) ?? candidates.find(item => item.api === model.api) ?? candidates[0];
    const api = model.api ?? (!model.type || model.type === "chat" ? config.api : undefined) ?? defaults?.api;
    const baseUrl = model.baseUrl ?? config.baseUrl ?? defaults?.baseUrl;
    if (typeof api !== "string" || !api.trim() || typeof baseUrl !== "string" || !baseUrl.trim()) throw new Error(`Provider '${id}', model '${model.id}': api and baseUrl are required.`);
  }
  for (const model of Object.values(config.modelOverrides ?? {})) validateModelMetadata(model);
}

/** 【CodingAgent】【模型元数据】@param {object} model 模型或覆盖配置 @returns {void} 同步拒绝错误形状及数值，避免扩展目录提前发生变化 */
function validateModelMetadata(model) {
  /** 【CodingAgent】【对象校验】@param {unknown} value 可选值 @param {string} name 诊断路径 @returns {void} 检查非空对象 */
  const object = (value, name) => {
    if (value !== undefined && (!value || typeof value !== "object" || Array.isArray(value))) throw new Error(`${name} must be an object.`);
  };
  /** 【CodingAgent】【整数校验】@param {unknown} value 可选值 @param {string} name 诊断路径 @param {number} maximum 上限 @returns {void} 检查正整数 */
  const integer = (value, name, maximum) => {
    if (value !== undefined && (!Number.isInteger(value) || value < 1 || value > maximum)) throw new Error(`${name} must be an integer between 1 and ${maximum}.`);
  };
  // 1. 【CodingAgent】【图片限制】保持与宿主整数类型及上游尺寸、质量范围一致
  object(model.inputLimits, "inputLimits");
  const limits = model.inputLimits;
  integer(limits?.maxRequestBytes, "inputLimits.maxRequestBytes", 9223372036854775807);
  object(limits?.images, "inputLimits.images");
  const images = limits?.images;
  integer(images?.maxPerMessage, "inputLimits.images.maxPerMessage", 2147483647);
  integer(images?.maxPerRequest, "inputLimits.images.maxPerRequest", 2147483647);
  object(images?.resize, "inputLimits.images.resize");
  for (const [name, maximum] of [["maxWidth", 2147483647], ["maxHeight", 2147483647], ["maxBytes", 9223372036854775807], ["jpegQuality", 100]])
    integer(images?.resize?.[name], `inputLimits.images.resize.${name}`, maximum);
  // 2. 【CodingAgent】【缓存寿命】显式空值、非数值及无限值不能成为可用的缓存期限
  object(model.promptCache, "promptCache");
  for (const name of ["short", "long"]) {
    const value = model.promptCache?.[name];
    if (value !== undefined && (!Number.isFinite(value) || value <= 0)) throw new Error(`promptCache.${name} must be a finite number greater than zero.`);
  }
}

/** 【CodingAgent】【目录同步】@param {object} runtime 会话快照 @param {string} id 提供方 @param {object|undefined} config 新配置或注销 @returns {void} 同步本次调用的只读目录 */
function updateProviderSnapshot(runtime, id, config) {
  runtime.providerBaselines ??= {};
  const baseline = runtime.providerBaselines[id] ?? (runtime.physicalModels ?? runtime.allModels ?? runtime.models).filter(model => model.provider === id);
  let models = baseline;
  if (config?.models) models = config.models.map(model => {
    const candidates = baseline.filter(item => (item.type ?? "chat") === (model.type ?? "chat"));
    const defaults = candidates.find(item => item.id === model.id) ?? candidates.find(item => item.api === model.api) ?? candidates[0];
    const api = model.api ?? (!model.type || model.type === "chat" ? config.api : undefined) ?? defaults?.api;
    const baseUrl = model.baseUrl ?? config.baseUrl ?? defaults?.baseUrl;
    if (!api || !baseUrl) throw new Error(`Provider '${id}', model '${model.id}': api and baseUrl are required.`);
    return { ...model, provider: id, api, baseUrl, headers: undefined };
  });
  else if (config?.baseUrl) models = baseline.map(model => ({ ...model, baseUrl: config.baseUrl }));
  runtime.providerBaselines[id] = cloneSessionValue(baseline);
  runtime.physicalModels = (runtime.physicalModels ?? runtime.allModels ?? runtime.models).filter(model => model.provider !== id).concat(cloneSessionValue(models));
  updateVirtualSnapshot(runtime, { id, registered: config !== undefined });
  if (config?.apiKey && !runtime.availableProviders.includes(id)) runtime.availableProviders.push(id);
}

/**
 * 【CodingAgent】【扩展调用】在持久扩展实例上执行独立请求
 * @param {object} payload 当前命令、事件或工具参数
 * @returns {Promise<void>} 完成后通过协议输出响应
 */
async function main(payload) {
  RegistryAssistantMessageStream ??= (await import(virtualModuleUrl("@mariozechner/pi-ai"))).AssistantMessageEventStream;
  if (payload.mode === "resetModules") {
    extensions.clear();
    readyExtensions.clear();
    userBashOperations.clear();
    extensionProviders.clear();
    virtualModels.clear();
    mcpServers.clear();
    providerVersions.clear();
    extensionEvents.removeAllListeners();
    write({ ok: true });
    return;
  }
  const { commandMap, toolMap, flagMap, shortcutMap, handlerMap, messageRendererMap, entryRendererMap, markdownRegistration, unsupported, api } = await loadExtension(payload);
  requestContext.getStore().sessionReady = !["load", "providerAuth", "modelRegistryCallback", "capabilityProvider"].includes(payload.mode) && payload.event?.type !== "project_trust";
  const { actions } = requestContext.getStore();

  if (payload.mode === "releaseUserBash") {
    const id = payload.toolArgs.operationId;
    if (userBashOperations.get(id)?.filePath === payload.filePath) userBashOperations.delete(id);
    write({ ok: true });
    return;
  }

  if (payload.mode === "executeUserBash") {
    const request = payload.toolArgs;
    const entry = userBashOperations.get(request.operationId);
    userBashOperations.delete(request.operationId);
    if (!entry || entry.filePath !== payload.filePath || entry.sessionId !== payload.session?.header.id)
      throw new Error("User bash operations are no longer available for this session.");
    const decoder = new (require("node:string_decoder").StringDecoder)("utf8");
    /** 【CodingAgent】【命令增量】@param {string} text 已解码文本 @returns {void} 向对应宿主命令发送增量 */
    const emit = text => {
      const id = requestContext.getStore().id;
      if (text && activeRequests.has(id)) process.stdout.write(toolUpdatePrefix + JSON.stringify({ requestId: id, update: { text } }) + "\n");
    };
    try {
      const result = await entry.operations.exec(request.command, request.cwd, {
        signal: requestContext.getStore().controller.signal,
        onData: data => emit(typeof data === "string" ? data : decoder.write(Buffer.from(data))),
      });
      emit(decoder.end());
      write({ ok: true, exitCode: result?.exitCode ?? null, actions });
    } catch (error) { emit(decoder.end()); throw error; }
    return;
  }

  if (payload.mode === "modelRegistryCallback") {
    const request = payload.toolArgs;
    const entry = modelRegistryCallbacks.get(request.callId);
    if (!entry) throw new Error("Model registry callback is no longer active.");
    const name = { payload: "onPayload", response: "onResponse", streamEvent: "onProviderStreamEvent", headers: "transformHeaders" }[request.kind];
    const value = await requestContext.run(entry.context, () => entry.options[name](request.value, request.model));
    write({ ok: true, value: value ?? (["payload", "headers"].includes(request.kind) ? request.value : null) });
    return;
  }

  if (payload.mode === "providerAuth") {
    const request = payload.toolArgs;
    const registration = extensionProviders.get(request.providerId);
    const signal = requestContext.getStore().controller.signal;
    if (!registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Auth provider registration has changed.");
    if (request.operation === "filterModels") {
      const provider = registration.config.canonical;
      const credential = request.credential ?? undefined;
      const physical = request.models.filter(model => model.api !== "pi-virtual");
      // 1. 【CodingAgent】【聊天过滤】宿主菜单只执行聊天规则，跨能力过滤不参与聊天快照
      const models = provider?.filterModels?.(physical, credential) ?? physical;
      if (signal.aborted || extensionProviders.get(request.providerId) !== registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Auth provider registration has changed or was cancelled.");
      write({ ok: true, value: [...models, ...request.models.filter(model => model.api === "pi-virtual")] });
      return;
    }
    if (request.operation === "availableModels" || request.operation === "checkAuth") {
      const provider = registration.config.canonical;
      const credential = request.credential ?? undefined;
      const apiKey = provider?.auth?.apiKey;
      const configured = credential?.type === "oauth" ? !!provider?.auth?.oauth : apiKey?.check
        ? await apiKey.check({ ctx: createProviderAuthContext(request.environment), credential, signal })
        : apiKey?.resolve ? await apiKey.resolve({ ctx: createProviderAuthContext(request.environment), credential, signal }) : undefined;
      if (request.operation === "checkAuth") {
        // 1. 【CodingAgent】【认证预检】仅检查资格，不调用模型过滤或把请求确认成已经开始
        if (signal.aborted || extensionProviders.get(request.providerId) !== registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Auth provider registration has changed or was cancelled.");
        write({ ok: true, value: !!configured });
        return;
      }
      let models = configured ? request.models.filter(model => model.api !== "pi-virtual") : [];
      const virtuals = configured ? request.models.filter(model => model.api === "pi-virtual") : [];
      if (configured) {
        if (provider.filterAllModels) models = provider.filterAllModels(models, credential);
        else if (provider.filterModels) {
          const chat = models.filter(model => !model.type || model.type === "chat");
          const allowed = new Set(provider.filterModels(chat, credential).map(model => model.id));
          models = models.filter(model => model.type && model.type !== "chat" || allowed.has(model.id));
        }
      }
      if (signal.aborted || extensionProviders.get(request.providerId) !== registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Auth provider registration has changed or was cancelled.");
      write({ ok: true, value: [...models, ...virtuals] });
      return;
    }
    if (request.operation === "resolveApiKey") {
      const apiKey = registration.config.canonical?.auth?.apiKey;
      if (typeof apiKey?.resolve !== "function") throw new Error("Provider API key resolver is unavailable.");
      const value = await apiKey.resolve({ ctx: createProviderAuthContext(request.environment), credential: request.apiKeyCredential ?? undefined, signal });
      if (signal.aborted || extensionProviders.get(request.providerId) !== registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Auth provider registration has changed or was cancelled.");
      write({ ok: true, value: value ?? null });
      return;
    }
    if (request.operation !== "loginApiKey" && !registration.config.oauth) throw new Error("OAuth provider is unavailable.");
    const oauth = registration.config.oauth;
    const loginAuth = request.operation === "loginApiKey" ? registration.config.canonical?.auth?.apiKey : oauth;
    let value;
    if (request.operation === "login" || request.operation === "loginApiKey") {
      const loginOptions = createProviderLoginOptions(request);
      let notifications = Promise.resolve();
      /** 【CodingAgent】【OAuth 通知】@param {string} kind 回调类型 @param {*} value 参数 @returns {void} 按调用顺序发送同步通知 */
      const notify = (kind, value) => {
        notifications = notifications.then(() => requestProviderService("providerAuthCallback", { callId: request.callId, kind, value }));
        void notifications.catch(() => {});
      };
      /** 【CodingAgent】【OAuth 输入】@param {string} kind 回调类型 @param {*} value 参数 @returns {Promise<*>} 等待通知后请求用户输入 */
      const input = async (kind, value = null, promptSignal) => {
        await notifications;
        return requestProviderService("providerAuthCallback", { callId: request.callId, kind, value }, promptSignal);
      };
      if (request.operation === "loginApiKey" || registration.config.canonical?.auth?.oauth) {
        if (typeof loginAuth?.login !== "function") throw new Error("Provider login is unavailable.");
        value = await loginAuth.login({ signal,
          notify: event => {
            if (event.type === "auth_url") notify("auth", event);
            else if (event.type === "device_code") notify("deviceCode", event);
            else if (event.type === "info") notify("info", event);
            else notify("progress", event.message + (event.links?.length ? "\n" + event.links.map(link => `${link.label ?? link.url}: ${link.url}`).join("\n") : ""));
          },
          prompt: async prompt => {
            const kind = { text: "prompt", secret: "secret", select: "select", manual_code: "manualTyped" }[prompt.type];
            if (!kind) throw new Error("Unsupported auth prompt type: " + prompt.type);
            const response = await input(kind, { ...prompt, signal: undefined }, prompt.signal);
            if (response === undefined) throw new Error("Login cancelled.");
            return response;
          }
        }, loginOptions);
      } else value = await oauth.login({ signal, onAuth: info => notify("auth", info), onDeviceCode: info => notify("deviceCode", info),
          onProgress: message => notify("progress", message), onPrompt: prompt => input("prompt", prompt),
          onManualCodeInput: () => input("manual"), onSelect: prompt => input("select", prompt) }, loginOptions);
      await notifications;
    } else if (request.operation === "refreshToken") value = await oauth.refreshToken(request.credentials, signal);
    else if (request.operation === "getApiKey") value = await oauth.getApiKey(request.credentials);
    else if (request.operation === "toAuth") value = oauth.toAuth ? await oauth.toAuth(request.credentials) : { apiKey: await oauth.getApiKey(request.credentials) };
    else throw new Error("Unknown OAuth operation.");
    if (signal.aborted) throw new Error("OAuth operation cancelled.");
    if (extensionProviders.get(request.providerId) !== registration || providerVersions.get(request.providerId) !== request.version) throw new Error("OAuth provider registration has changed.");
    write({ ok: true, value });
    return;
  }

  if (payload.mode === "refreshProvider") {
    const request = payload.toolArgs;
    const registration = extensionProviders.get(request.providerId);
    const signal = requestContext.getStore().controller.signal;
    const current = () => !signal.aborted && extensionProviders.get(request.providerId) === registration && providerVersions.get(request.providerId) === request.version;
    if (!registration || !current()) { write({ ok: true }); return; }
    let apiKeyAuthStatus = registration.apiKeyAuthStatus;
    const apiKey = registration.config.canonical?.auth?.apiKey;
    /** 【CodingAgent】【刷新快照】@param {object[]} models 原生源目录 @returns {void} 验证并同步可序列化目录及认证元数据 */
    const updateModels = models => {
      const sourceModels = cloneSessionValue(models);
      if (request.credential?.type === "oauth" && typeof registration.config.oauth?.modifyModels === "function") {
        const normalized = models.map(model => {
          const defaults = request.models.find(entry => entry.id === model.id && (entry.type ?? "chat") === (model.type ?? "chat"));
          return { ...defaults, ...model, provider: request.providerId,
            api: model.api ?? registration.config.api ?? defaults?.api, baseUrl: model.baseUrl ?? registration.config.baseUrl ?? defaults?.baseUrl };
        });
        models = [...registration.config.oauth.modifyModels(normalized.filter(model => !model.type || model.type === "chat"), request.credential),
          ...normalized.filter(model => model.type && model.type !== "chat")];
      }
      if (models !== undefined) {
        const config = { ...registration.config, models };
        if (!Array.isArray(models)) throw new Error("refreshModels must return a model array.");
        validateProviderConfig(request.providerId, config, payload.providerDefaults?.[request.providerId] ?? []);
        registration.config = config; registration.sourceModels = sourceModels;
        registration.apiKeyAuthStatus = apiKeyAuthStatus; registration.credential = request.credential ?? undefined;
      }
    };
    /** 【CodingAgent】【目录发布】@param {object} publication 持久化与同步更新 @returns {Promise<boolean>} 宿主是否已接受持久数据和更新后的目录 */
    const publish = publication => {
      const previous = providerPublicationChains.get(request.providerId) ?? Promise.resolve();
      const queued = previous.catch(() => {}).then(async () => {
        if (!current()) return false;
        // 1. 【CodingAgent】【持久发布】缓存保存和代次校验成功后才能运行提供方的同步更新
        const accepted = await requestProviderPublication({ callId: request.callId,
          ...(Object.hasOwn(publication, "persist") ? { persist: publication.persist } : {}) });
        if (!accepted || !current()) return false;
        publication.update?.();
        if (registration.config.canonical)
          updateModels(registration.config.canonical.getAllModels?.() ?? registration.config.canonical.getModels?.() ?? []);
        // 2. 【CodingAgent】【即时目录】每次成功发布均同步宿主，刷新后续失败不会撤销已提交的目录
        return await requestProviderPublication({ callId: request.callId,
          provider: serializeProviders().find(provider => provider.id === request.providerId) });
      });
      // 3. 【CodingAgent】【发布顺序】同一提供方串行执行完整发布，失败不阻断下一次发布
      const tail = queued.catch(() => {});
      providerPublicationChains.set(request.providerId, tail);
      void tail.then(() => { if (providerPublicationChains.get(request.providerId) === tail) providerPublicationChains.delete(request.providerId); });
      return queued;
    };
    const result = request.metadataOnly ? undefined : await registration.config.refreshModels?.({
      credential: request.credential ?? undefined, stored: request.stored ?? undefined,
      allowNetwork: request.allowNetwork === true, force: request.force, signal, publish });
    if (current()) {
      if (registration.config.canonical) await publish({});
      else await publish({ update: () => updateModels(result ?? registration.sourceModels ?? registration.config.models ?? request.models) });
      // 4. 【CodingAgent】【缓存先行】已发布目录不受后续认证检查失败影响，联网准备阶段由宿主独立解析认证
      if (request.checkAuth !== false && apiKey && request.credential?.type !== "oauth") {
        const input = { ctx: createProviderAuthContext(), credential: request.credential?.type === "api_key" ? request.credential : undefined, signal };
        apiKeyAuthStatus = undefined;
        if (typeof apiKey.check === "function") apiKeyAuthStatus = await apiKey.check(input);
        else if (typeof apiKey.resolve === "function") {
          const resolved = await apiKey.resolve(input);
          if (resolved) apiKeyAuthStatus = { type: "api_key", source: resolved.source };
        }
        if (current()) await publish({});
      }
    }
    write({ ok: true, providers: serializeProviders() });
    return;
  }

  if (payload.mode === "routeVirtualModel") {
    const request = payload.toolArgs, key = request.model.provider + "\0" + request.model.id;
    const entry = virtualModels.get(key);
    if (!entry || entry.version !== request.version) throw new Error("Virtual model registration changed.");
    const signal = requestContext.getStore().controller.signal;
    const state = readyExtensions.get(path.resolve(entry.filePath));
    const value = await entry.route({ ...request, signal }, createCommandContext(state.api, payload, []));
    if (signal.aborted || virtualModels.get(key) !== entry) throw new Error("Virtual model registration changed or was cancelled.");
    write({ ok: true, value, stateChanged: value.state !== undefined && value.state !== request.state });
    return;
  }

  if (payload.mode === "capabilityProvider") {
    const request = payload.toolArgs;
    const registration = extensionProviders.get(request.providerId);
    if (!registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Provider registration changed during capability request.");
    const provider = registration.config.canonical;
    const method = request.type === "image" ? "generateImages" : "classify";
    const options = { ...request.options, signal: requestContext.getStore().controller.signal };
    const callback = modelRegistryCallbacks.get(request.callId);
    for (const name of ["onPayload", "onResponse"])
      if (typeof callback?.options[name] === "function") options[name] = (value, model) => requestContext.run(callback.context, () => callback.options[name](value, model));
    const value = await provider[method](request.model, request.context, options);
    if (extensionProviders.get(request.providerId) !== registration || providerVersions.get(request.providerId) !== request.version) throw new Error("Provider registration changed during capability request.");
    write({ ok: true, value });
    return;
  }

  if (payload.mode === "streamProvider") {
    const request = payload.toolArgs;
    const registration = extensionProviders.get(request.providerId);
    const implementation = registration?.config.canonical && request.simple === false ? registration.config.stream : registration?.config.streamSimple;
    if (typeof implementation !== "function") throw new Error("Extension stream provider is not registered: " + request.providerId);
    const { id, controller } = requestContext.getStore();
    const options = { ...request.options, signal: controller.signal };
    for (const [kind, name] of Object.entries({ payload: "onPayload", response: "onResponse", streamEvent: "onProviderStreamEvent", headers: "transformHeaders" }))
      if (request.callbacks[kind]) options[name] = value => requestHost("providerCallback", { callId: request.callId, kind, value }, false, { signal: controller.signal });
    // 1. 【CodingAgent】【扩展流式】逐帧传输快照，避免扩展随后修改 partial 影响已经发出的事件
    const stream = await implementation(request.model, request.context, options);
    if (!stream || typeof stream[Symbol.asyncIterator] !== "function") throw new Error("streamSimple must return an async iterable event stream.");
    let terminal = false;
    for await (const event of stream) {
      if (!activeRequests.has(id)) break;
      process.stdout.write(toolUpdatePrefix + JSON.stringify({ requestId: id, update: event }) + "\n");
      if (event.type === "done" || event.type === "error") { terminal = true; break; }
    }
    if (!terminal) throw new Error("Extension provider ended without a terminal event.");
    write({ ok: true, actions });
    return;
  }

  if (payload.mode === "prepareToolLoadout") {
    try {
      const changes = toolMap.get(payload.toolName)?.prepareLoadout?.(createToolLoadout(payload.toolArgs));
      write({ ok: true, changes });
    } catch (error) { write({ ok: false, error: String(error?.message ?? error) }); }
    return;
  }

  const commands = Array.from(commandMap.values()).map(command => ({
    name: command.name,
    description: typeof command.options.description === "string" ? command.options.description : "",
    argumentHint: typeof command.options.argumentHint === "string" ? command.options.argumentHint : undefined,
    hasHandler: typeof command.options.handler === "function"
  }));

  const tools = Array.from(toolMap.values()).map(serializeToolDefinition);

  const flags = Array.from(flagMap.values()).map(flag => {
    const defaultValue = flag.options && Object.prototype.hasOwnProperty.call(flag.options, "default")
      ? flag.options.default
      : undefined;
    return {
      name: flag.name,
      description: typeof flag.options?.description === "string" ? flag.options.description : "",
      type: flag.options?.type === "boolean" || flag.options?.type === "string" ? flag.options.type : "",
      default: typeof defaultValue === "boolean" || typeof defaultValue === "string" ? defaultValue : undefined
    };
  });

  const shortcuts = Array.from(shortcutMap.values()).map(shortcut => ({
    shortcut: shortcut.shortcut,
    description: typeof shortcut.options?.description === "string" ? shortcut.options.description : "",
    hasHandler: typeof shortcut.options?.handler === "function"
  }));

  const eventHandlers = Array.from(handlerMap.entries())
    .filter(entry => entry[1].length > 0)
    .map(entry => entry[0]);

  const messageRenderers = Array.from(messageRendererMap.entries()).map(([customType, renderer]) => ({
    customType,
    hasRenderer: typeof renderer === "function"
  }));

  if (payload.mode === "load") {
    const entryRenderers = Array.from(entryRendererMap.entries()).map(([customType, renderer]) => ({ customType, hasRenderer: typeof renderer === "function" }));
    write({ ok: true, commands, tools, flags, shortcuts, eventHandlers, messageRenderers, entryRenderers, hasMarkdownTransformer: typeof markdownRegistration.transformer === "function", unsupported, providers: serializeProviders(), mcpServers: [...mcpServers.values()] });
    return;
  }

  if (payload.mode === "transformMarkdown") {
    const request = payload.toolArgs;
    let markdown = request.markdown;
    // 1. 【CodingAgent】【Markdown 转换】与上游一致只接受同步字符串结果，单个转换器错误保留当前正文
    try {
      if (typeof markdownRegistration.transformer === "function") {
        const result = markdownRegistration.transformer(markdown, request.context);
        if (typeof result === "string") markdown = result;
      }
    } catch { }
    write({ ok: true, markdown });
    return;
  }

  if (payload.mode === "renderEntry") {
    const entry = payload.toolArgs?.entry;
    const renderer = entryRendererMap.get(entry?.customType);
    if (typeof renderer !== "function") {
      write({ ok: false, error: "Extension entry renderer was not registered: " + entry?.customType });
      return;
    }
    // 1. 【CodingAgent】【条目显示】传入原始条目和展开状态，组件按宿主给定宽度生成文本
    const rendered = await renderer(entry, { expanded: payload.expanded === true }, themeProxy);
    const width = Number.isInteger(payload.toolArgs?.width) && payload.toolArgs.width > 0 ? payload.toolArgs.width : 80;
    write({ ok: true, lines: normalizeComponentLines(rendered, width) ?? [] });
    return;
  }

  if (payload.mode === "renderMessage") {
    const key = String(payload.customType ?? "");
    const renderer = messageRendererMap.get(key);
    if (typeof renderer !== "function") {
      write({ ok: false, error: "Extension message renderer was not registered: " + key });
      return;
    }

    const message = {
      role: "custom",
      customType: key,
      content: normalizeContentBlocks(payload.messageContent),
      display: payload.messageDisplay !== false,
      details: Object.prototype.hasOwnProperty.call(payload, "messageDetails") ? payload.messageDetails : undefined,
      timestamp: typeof payload.messageTimestamp === "number" ? payload.messageTimestamp : Date.now()
    };

    const rendered = await renderer(message, { expanded: payload.expanded === true }, themeProxy);
    write({ ok: true, lines: normalizeComponentLines(rendered) ?? [] });
    return;
  }

  if (payload.mode === "invokeShortcut") {
    const shortcut = shortcutMap.get(String(payload.shortcut ?? ""));
    if (!shortcut) {
      write({ ok: false, error: "Extension shortcut was not registered: " + String(payload.shortcut ?? "") });
      return;
    }
    if (typeof shortcut.options.handler !== "function") {
      write({ ok: false, error: "Extension shortcut has no handler: " + shortcut.shortcut });
      return;
    }

    const returnValue = await shortcut.options.handler(createCommandContext(api, payload, actions));
    const returnText = returnValue === undefined || returnValue === null ? undefined : toText(returnValue);
    if (!payload.deferMessageRendering) await renderCustomMessageActions(actions, messageRendererMap);
    write({ ok: true, actions, returnText, unsupported });
    return;
  }

  if (payload.mode === "emitToolCall") {
    const handlers = handlerMap.get("tool_call") ?? [];
    const event = {
      type: "tool_call",
      parentToolCallId: payload.parentToolCallId,
      toolName: String(payload.toolName ?? ""),
      toolCallId: String(payload.toolCallId ?? ""),
      input: payload.toolArgs && typeof payload.toolArgs === "object" ? payload.toolArgs : {}
    };

    let result = undefined;
    for (const handler of handlers) {
      const handlerResult = await handler(event, createCommandContext(api, payload, actions));
      if (handlerResult) {
        result = handlerResult;
        if (result.block) break;
      }
    }

    write({
      ok: true,
      block: result && result.block === true,
      reason: result && typeof result.reason === "string" ? result.reason : undefined,
      terminate: result && result.terminate === true,
      input: event.input,
      actions
    });
    return;
  }

  if (payload.mode === "emitToolResult") {
    const handlers = handlerMap.get("tool_result") ?? [];
    const event = {
      type: "tool_result",
      parentToolCallId: payload.parentToolCallId,
      toolName: String(payload.toolName ?? ""),
      toolCallId: String(payload.toolCallId ?? ""),
      input: payload.toolArgs && typeof payload.toolArgs === "object" ? payload.toolArgs : {},
      content: normalizeContentBlocks(payload.toolResult?.content),
      details: payload.toolResult && Object.prototype.hasOwnProperty.call(payload.toolResult, "details")
        ? payload.toolResult.details
        : undefined,
      isError: payload.toolResult?.isError === true,
      usage: payload.toolResult?.usage,
      structuredContent: payload.toolResult?.structuredContent
    };

    for (const handler of handlers) {
      try {
        const handlerResult = await handler(event, createCommandContext(api, payload, actions));
        if (!handlerResult) continue;
        if (handlerResult.content !== undefined) {
          event.content = normalizeContentBlocks(handlerResult.content);
          // 1. 【CodingAgent】【结果一致性】替换展示内容却没有对应结构化结果时，丢弃不再匹配的旧结构化值
          if (handlerResult.structuredContent === undefined) delete event.structuredContent;
        }
        if (handlerResult.details !== undefined) {
          event.details = handlerResult.details;
        }
        if (handlerResult.structuredContent !== undefined) event.structuredContent = handlerResult.structuredContent;
        if (handlerResult.usage !== undefined) event.usage = handlerResult.usage;
        if (handlerResult.isError !== undefined) {
          event.isError = handlerResult.isError === true;
        }
      } catch {
      }
    }

    write({
      ok: true,
      content: normalizeMessageContent(event.content),
      isError: event.isError,
      details: event.details,
      usage: event.usage,
      structuredContent: event.structuredContent,
      actions
    });
    return;
  }

  if (payload.mode === "emitEvent") {
    const event = payload.event && typeof payload.event === "object" ? payload.event : { type: "" };
    const eventType = String(event.type ?? "");
    const handlers = handlerMap.get(eventType) ?? [];
    const handlerErrors = [];
    if (eventType === "cache_warming_decision") {
      let overrideAction;
      for (const handler of handlers.slice()) {
        try {
          const result = await handler(event, createCommandContext(api, payload, actions));
          if (result?.action === "warm" || result?.action === "stop") overrideAction = result.action;
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, transformedEvent: { ...event, overrideAction } });
      return;
    }
    if ((eventType === "turn_end" || eventType === "agent_before_settle") && event.context && Object.prototype.hasOwnProperty.call(event, "entries")) {
      let entries = event.entries, shouldContinue = event.continue, context = event.context, valid = event.valid !== false;
      for (const handler of handlers.slice()) {
        try {
          const result = await handler({ ...event, entries, continue: shouldContinue, context }, createCommandContext(api, payload, actions));
          if (result?.entries !== undefined) entries = result.entries;
          if (result?.continue !== undefined) shouldContinue = result.continue;
        } catch (error) { handlerErrors.push(formatError(error)); }
        try {
          // 1. 【CodingAgent】【边界消息】先交付处理器排入的消息，预览必须反映最新队列且不得重复发送
          const deliveries = actions.filter(action => ["sendMessage", "userMessage", "customMessage"].includes(action.type));
          for (const action of deliveries) actions.splice(actions.indexOf(action), 1);
          context = await requestHost("previewBoundary", { boundary: eventType, entries, actions: deliveries, filePath: payload.filePath });
          valid = true;
        } catch (error) {
          valid = false;
          handlerErrors.push("Invalid boundary entries: " + formatError(error));
        }
      }
      // 2. 【CodingAgent】【跨模块草稿】保留无效草稿交给后续模块修复，仅由最终提交阶段丢弃
      write({ ok: true, handlerErrors, actions, transformedEvent: { ...event, entries, continue: shouldContinue, context, valid } });
      return;
    }
    if (eventType === "resources_discover") {
      const discovered = { skillPaths: [], promptPaths: [], themePaths: [] };
      for (const handler of handlers.slice()) {
        try {
          const result = await handler({ ...event }, createCommandContext(api, payload, actions));
          for (const key of Object.keys(discovered)) {
            if (result?.[key] === undefined) continue;
            if (!Array.isArray(result[key]) || result[key].some(path => typeof path !== "string")) throw new Error(`Invalid resources_discover ${key}`);
            discovered[key].push(...result[key]);
          }
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, transformedEvent: { ...event, ...discovered } });
      return;
    }
    if (eventType === "user_bash") {
      const { _tauOperationId: operationId, ...userEvent } = event;
      let handlerResult;
      for (const handler of handlers.slice()) {
        const result = await handler(userEvent, createCommandContext(api, payload, actions));
        requestContext.getStore().controller.signal.throwIfAborted();
        if (result === undefined) continue;
        const hasOperations = result && typeof result === "object" && result.operations !== undefined;
        const hasResult = result && typeof result === "object" && result.result !== undefined;
        const completed = result?.result;
        const valid = hasOperations !== hasResult && (hasOperations
          ? result.operations && typeof result.operations === "object" && typeof result.operations.exec === "function"
          : completed && typeof completed === "object" && typeof completed.output === "string" && "exitCode" in completed &&
            (completed.exitCode === undefined || typeof completed.exitCode === "number") &&
            typeof completed.cancelled === "boolean" && typeof completed.truncated === "boolean" &&
            (completed.fullOutputPath === undefined || typeof completed.fullOutputPath === "string"));
        if (!valid) throw new Error("Invalid user_bash handler result: return undefined or exactly one valid { operations } or { result } object");
        if (hasOperations) {
          userBashOperations.set(operationId, { operations: result.operations, filePath: payload.filePath, sessionId: payload.session?.header.id });
          handlerResult = { operationId };
        } else handlerResult = { result: completed };
        break;
      }
      write({ ok: true, actions, transformedEvent: { ...userEvent, handlerResult } });
      return;
    }
    if (eventType === "project_trust") {
      const ui = createUiContext(actions, payload);
      const context = { cwd: payload.cwd, mode: payload.extensionMode ?? "print", hasUI: payload.hasExtensionUi === true,
        ui: { select: ui.select, confirm: ui.confirm, input: ui.input, notify: ui.notify } };
      let handlerResult;
      for (const handler of handlers.slice()) {
        try {
          const result = await handler(event, context);
          if (!result || !["yes", "no", "undecided"].includes(result.trusted)) throw new Error("Invalid project_trust result");
          if (result.trusted === "undecided") continue;
          handlerResult = result;
          break;
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, transformedEvent: { ...event, handlerResult } });
      return;
    }
    if (["session_before_switch", "session_before_fork", "session_before_tree"].includes(eventType)) {
      let handlerResult = event.handlerResult;
      if (eventType === "session_before_tree") event.signal = requestContext.getStore().controller.signal;
      for (const handler of handlers.slice()) {
        try {
          const result = await handler(event, createCommandContext(api, payload, actions));
          if (result) handlerResult = result;
          if (handlerResult?.cancel) break;
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      const { signal, ...serializable } = event;
      write({ ok: true, handlerErrors, actions, transformedEvent: { ...serializable, handlerResult } });
      return;
    }
    if (eventType === 'session_before_compact') {
      event.signal = requestContext.getStore().controller.signal;
      const ops = event.preparation.fileOps;
      event.preparation.fileOps = { read: new Set(ops.read), written: new Set(ops.written), edited: new Set(ops.edited) };
      let handlerResult = event.handlerResult;
      for (const handler of handlers.slice()) {
        try {
          const result = await handler(event, createCommandContext(api, payload, actions));
          if (result) handlerResult = result;
          if (handlerResult?.cancel) break;
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      event.preparation.fileOps = Object.fromEntries(Object.entries(event.preparation.fileOps).map(([key, value])=>[key,[...value]]));
      const { signal, ...serializable } = event;
      write({ ok:true, handlerErrors, actions, transformedEvent: {...serializable, handlerResult} });
      return;
    }
    if (eventType === "input") {
      let text = event.text, images = event.images, action = "continue";
      for (const handler of handlers.slice()) {
        try {
          const result = await handler({ ...event, text, images }, createCommandContext(api, payload, actions));
          if (result?.action === "handled") { action = "handled"; break; }
          if (result?.action === "transform") {
            if (typeof result.text !== "string" || (result.images != null && !Array.isArray(result.images))) throw Error("Invalid input transform result.");
            text = result.text;
            images = result.images ?? images;
            action = "transform";
          }
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, unsupported, transformedEvent: { ...event, text, images, action } });
      return;
    }
    if (eventType === "before_agent_start") {
      const options = event.systemPromptOptions;
      const messages = [];
      /** 【CodingAgent】【启动提示】@returns {string} 当前处理器已经修改后的完整提示 */
      const getPrompt = () => typeof options.forceSystemPrompt === "string" ? options.forceSystemPrompt
        : Object.values(buildPromptSections(options, event.promptDefaults)).join("\n\n");
      const context = createCommandContext(api, payload, actions);
      context.getSystemPrompt = getPrompt;
      Object.defineProperty(event, "systemPrompt", { get: getPrompt, enumerable: true });
      for (const handler of handlers.slice()) {
        try {
          const result = await handler(event, context);
          if (result?.message) messages.push(normalizeAgentMessage({ ...result.message, role: "custom" }));
          if (result?.systemPrompt !== undefined) options.forceSystemPrompt = result.systemPrompt;
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, unsupported, transformedEvent: { type: eventType, systemPromptOptions: options, messages } });
      return;
    }
    if (eventType === "context" || eventType === "context_with_system") {
      const transformedEvent = await transformContextEvent(event, handlers, createCommandContext(api, payload, actions), handlerErrors);
      write({ ok: true, handlerErrors, actions, unsupported, transformedEvent });
      return;
    }
    if (eventType === "before_provider_request") {
      let currentPayload = event.payload;
      for (const handler of handlers.slice()) {
        try {
          const result = await handler({ ...event, payload: currentPayload }, createCommandContext(api, payload, actions));
          if (result !== undefined) currentPayload = result;
        } catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, unsupported, transformedEvent: { ...event, payload: currentPayload } });
      return;
    }
    if (eventType === "before_provider_headers") {
      for (const handler of handlers.slice()) {
        try { await handler(event, createCommandContext(api, payload, actions)); }
        catch (error) { handlerErrors.push(formatError(error)); }
      }
      write({ ok: true, handlerErrors, actions, unsupported, transformedEvent: event });
      return;
    }
    if (eventType === "message_end") {
      let currentEvent = {
        ...event,
        message: normalizeAgentMessage(event.message)
      };
      let replacementMessage = undefined;
      for (const handler of handlers) {
        try {
          const handlerResult = await handler(currentEvent, createCommandContext(api, payload, actions));
          if (!handlerResult || !Object.prototype.hasOwnProperty.call(handlerResult, "message")) continue;
          const replacement = normalizeAgentMessage(handlerResult.message, currentEvent.message?.role);
          if (!replacement) continue;
          if (replacement.role !== currentEvent.message?.role) {
            handlerErrors.push("message_end handlers must return a message with the same role");
            continue;
          }
          currentEvent = { ...currentEvent, message: replacement };
          replacementMessage = replacement;
        } catch (error) {
          handlerErrors.push(formatError(error));
        }
      }

      write({ ok: true, handlerErrors, actions, unsupported, replacementMessage });
      return;
    }

    for (const handler of handlers) {
      try {
        await handler(event, createCommandContext(api, payload, actions));
      } catch (error) {
        handlerErrors.push(formatError(error));
      }
    }

    write({ ok: true, handlerErrors, actions, unsupported });
    return;
  }

  if (payload.mode === "prepareToolArguments") {
    const tool = toolMap.get(String(payload.toolName ?? ""));
    if (!tool) {
      write({ ok: false, error: "Extension tool was not registered: " + String(payload.toolName ?? "") });
      return;
    }

    if (typeof tool.prepareArguments !== "function") {
      write({ ok: true, preparedArgs: payload.toolArgs === undefined ? {} : payload.toolArgs });
      return;
    }

    const preparedArgs = await tool.prepareArguments(payload.toolArgs === undefined ? {} : payload.toolArgs);
    write({ ok: true, preparedArgs: preparedArgs === undefined ? {} : preparedArgs });
    return;
  }

  if (payload.mode === "executeTool") {
    const tool = toolMap.get(String(payload.toolName ?? ""));
    if (!tool) {
      write({ ok: false, error: "Extension tool was not registered: " + String(payload.toolName ?? "") });
      return;
    }
    if (typeof tool.execute !== "function") {
      write({ ok: false, error: "Extension tool has no execute handler: " + String(tool.name ?? payload.toolName ?? "") });
      return;
    }

    /** 【CodingAgent】【工具进度】@param {*} update 中间结果 @returns {void} 立即写入当前调用的独立更新通道 */
    const onUpdate = update => {
      const id = requestContext.getStore().id;
      if (activeRequests.has(id)) process.stdout.write(toolUpdatePrefix + JSON.stringify({ requestId: id, update: normalizeToolResult(update) }) + "\n");
    };
    const returnValue = await tool.execute(
      String(payload.toolCallId ?? ""),
      payload.toolArgs && typeof payload.toolArgs === "object" ? payload.toolArgs : {},
      requestContext.getStore().controller.signal,
      onUpdate,
      createCommandContext(api, payload, actions));
    const result = normalizeToolResult(returnValue);
    write({ ok: true, ...result, actions, unsupported });
    return;
  }

  const command = commandMap.get(String(payload.commandName ?? ""));
  if (!command) {
    write({ ok: false, error: "Extension command was not registered: " + String(payload.commandName ?? "") });
    return;
  }
  if (typeof command.options.handler !== "function") {
    write({ ok: false, error: "Extension command has no handler: " + command.name });
    return;
  }

  const returnValue = await command.options.handler(String(payload.args ?? ""), createCommandContext(api, payload, actions));
  const returnText = returnValue === undefined || returnValue === null ? undefined : toText(returnValue);
  pruneStaleActions(actions);
    if (!payload.deferMessageRendering) await renderCustomMessageActions(actions, messageRendererMap);
  write({ ok: true, actions, returnText, unsupported });
}
