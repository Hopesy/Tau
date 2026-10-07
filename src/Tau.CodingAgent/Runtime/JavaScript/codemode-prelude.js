// 作者：xxx
// 【CodingAgent】【脚本沙箱】由 pi-codemode 的 prelude-source.ts 迁移，使用 Jint 执行
/*
MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
(function (bridge, toolsJson, globalsJson, storeJson) {
	"use strict";
	const stringify = JSON.stringify;
	const parse = JSON.parse;
	const promiseThen = Promise.prototype.then;
	const ErrorCtor = Error;
	const TypeErrorCtor = TypeError;
	const RangeErrorCtor = RangeError;
	const pending = new Map();
	let nextId = 1;
	let finished = false;
	const EXIT = Object.freeze({});

	/** 【CodingAgent】【脚本沙箱】结束脚本并报告结果。 @param {*} ok 成功状态 @param {*} payload 结果或错误 @param {*} writes 状态写入 @returns {*} 操作结果 */

	function done(ok, payload, writes) {
		if (finished) return;
		finished = true;
		bridge("done", ok, payload, writes);
	}

	/** 【CodingAgent】【脚本沙箱】序列化可选脚本值。 @param {*} value 脚本值 @returns {*} 操作结果 */

	function serialize(value) {
		return value === undefined ? undefined : stringify(value);
	}

	/** 【CodingAgent】【脚本沙箱】保留错误名称和调用栈。 @param {*} error 错误对象 @returns {*} 操作结果 */

	function errorText(error) {
		const head = error.message ? error.name + ": " + error.message : String(error.name);
		const frames =
			typeof error.stack === "string"
				? error.stack.split("\n").filter((line) => line.trim() && !line.includes("codemode-prelude.js"))
				: [];
		return [head, ...frames].join("\n");
	}

	/** 【CodingAgent】【脚本沙箱】格式化控制台输出。 @param {*} value 脚本值 @returns {*} 操作结果 */

	function format(value) {
		if (typeof value === "string") return value;
		if (value instanceof ErrorCtor) return errorText(value);
		try {
			const json = stringify(value);
			return json === undefined ? String(value) : json;
		} catch {
			return String(value);
		}
	}

	/** 【CodingAgent】【脚本沙箱】序列化脚本错误。 @param {*} error 错误对象 @returns {*} 操作结果 */

	function describeError(error) {
		if (error instanceof ErrorCtor) {
			return stringify({ name: error.name, message: error.message, stack: errorText(error) });
		}
		return stringify({ message: format(error) });
	}

	/** 【CodingAgent】【脚本沙箱】建立异步宿主调用函数。 @param {*} kind 调用类型 @param {*} name 成员名称 @param {*} spread 是否展开参数 @returns {*} 操作结果 */

	function caller(kind, name, spread) {
		return (...args) =>
			new Promise((resolve, reject) => {
				let json;
				try {
					json = serialize(spread ? args : args[0]);
				} catch (error) {
					reject(error);
					return;
				}
				const id = nextId++;
				pending.set(id, { resolve, reject });
				bridge(kind, id, name, json);
			});
	}

	const tools = Object.create(null);
	const allTools = [];
	for (const { name, jsName, description } of parse(toolsJson)) {
		const fn = caller("call", name);
		if (!(jsName in tools)) {
			tools[jsName] = fn;
			allTools.push(Object.freeze({ name: jsName, description }));
		}
		if (!(name in tools)) tools[name] = fn;
	}
	Object.freeze(tools);
	Object.freeze(allTools);

	/** 【CodingAgent】【名称比较】规范名称以生成工具提示。 @param {string} name 名称 @returns {string} 规范名称 */
	const comparable = (name) => name.toLowerCase().replace(/[^a-z0-9]/g, "");
	/** 【CodingAgent】【脚本沙箱】为不存在的工具提供名称诊断。 @param {*} target 目标对象 @param {*} label 命名空间 @param {*} names 可用名称 @param {*} hint 辅助提示 @returns {*} 操作结果 */
	function guard(target, label, names, hint) {
		return new Proxy(target, {
			/** 【CodingAgent】【脚本沙箱】读取成员并提示相近名称。 @param {*} object 目标对象 @param {*} property 属性名 @param {*} receiver 代理接收方 @returns {*} 操作结果 */
			get(object, property, receiver) {
				if (typeof property !== "string" || property in object || property in Object.prototype || property === "then" || property === "toJSON") {
					return Reflect.get(object, property, receiver);
				}
				const wanted = comparable(property);
				const exact = names.filter((name) => comparable(name) === wanted);
				const close = exact.length > 0 ? exact : names.filter((name) => wanted && (comparable(name).includes(wanted) || wanted.includes(comparable(name))));
				let message = label + "." + property + " does not exist.";
				if (close.length > 0) message += " Did you mean " + close.slice(0, 5).map((name) => label + "." + name).join(", ") + "?";
				else if (names.length <= 20) message += " Available: " + names.join(", ") + ".";
				if (hint) message += " " + hint;
				message += ' Check for a member with "' + property + '" in ' + label + ".";
				throw new TypeErrorCtor(message);
			},
		});
	}
	const toolsProxy = guard(
		tools,
		"tools",
		allTools.map((tool) => tool.name),
		"ALL_TOOLS lists every tool; searchTools(query) finds tools by topic.",
	);

	const namespaces = new Map();
	for (const { name, spread } of parse(globalsJson)) {
		const fn = caller("global", name, spread);
		const dot = name.indexOf(".");
		if (dot === -1) {
			Object.defineProperty(globalThis, name, { value: fn, enumerable: true });
			continue;
		}
		const namespace = name.slice(0, dot);
		if (!namespaces.has(namespace)) namespaces.set(namespace, Object.create(null));
		namespaces.get(namespace)[name.slice(dot + 1)] = fn;
	}
	for (const [namespace, members] of namespaces) {
		Object.freeze(members);
		const value = guard(members, namespace, Object.keys(members));
		Object.defineProperty(globalThis, namespace, { value, enumerable: true });
	}

	const stored = new Map(Object.entries(parse(storeJson)));
	const writes = new Map();
	let storedChars = 0;
	for (const [key, json] of stored) storedChars += key.length + json.length;

	const STORE_HINT =
		"store() is for small state such as IDs or summaries. Show images with image(), keep large data in variables, or write it to a file with a tool.";

	/** 【CodingAgent】【脚本沙箱】校验持久状态键。 @param {*} name 成员名称 @param {*} key 状态键 @returns {*} 操作结果 */

	function checkKey(name, key) {
		if (typeof key !== "string") throw new TypeError(name + "() key must be a string");
	}

	/** 【CodingAgent】【脚本沙箱】写入或删除有界的 JSON 状态。 @param {*} key 状态键 @param {*} value 脚本值 @returns {*} 操作结果 */

	function store(key, value) {
		checkKey("store", key);
		const previous = stored.has(key) ? key.length + stored.get(key).length : 0;
		if (value === undefined) {
			stored.delete(key);
			storedChars -= previous;
			writes.set(key, undefined);
			return;
		}
		let json;
		try {
			json = stringify(value);
		} catch (error) {
			throw new TypeError("store(" + stringify(key) + ") value is not JSON-serializable: " + format(error));
		}
		if (json === undefined) {
			throw new TypeError("store(" + stringify(key) + ") value is not JSON-serializable");
		}
		if (json.length > 262144) {
			throw new RangeError(
				"store(" + stringify(key) + ") value has " + json.length + " characters of JSON, more than the limit of 262144. " +
					STORE_HINT,
			);
		}
		const next = storedChars - previous + key.length + json.length;
		if (next > 1048576) {
			throw new RangeError(
				"store is full: stored values would exceed 1048576 characters of JSON. Delete keys with store(key, undefined). " +
					STORE_HINT,
			);
		}
		stored.set(key, json);
		storedChars = next;
		writes.set(key, json);
	}

	/** 【CodingAgent】【脚本沙箱】读取独立的状态副本。 @param {*} key 状态键 @returns {*} 操作结果 */

	function load(key) {
		checkKey("load", key);
		const json = stored.get(key);
		return json === undefined ? undefined : parse(json);
	}

	/** 【CodingAgent】【脚本沙箱】序列化本次状态变更。 @returns {*} 操作结果 */

	function serializeWrites() {
		const entries = [];
		for (const [key, json] of writes) entries.push(json === undefined ? [key] : [key, json]);
		return stringify(entries);
	}

	Object.defineProperty(globalThis, "store", { value: store, enumerable: true });
	Object.defineProperty(globalThis, "load", { value: load, enumerable: true });

	let outputChars = 0;
	let outputItems = 0;

	/** 【CodingAgent】【脚本沙箱】检查输出总量并交付宿主。 @param {*} kind 调用类型 @param {*} data 输出数据 @param {*} mimeType 媒体类型 @returns {*} 操作结果 */

	function output(kind, data, mimeType) {
		if (finished) return;
		outputChars += data.length;
		outputItems++;
		if (outputChars > 16777216 || outputItems > 100000) {
			const error = new RangeErrorCtor(
				"script output exceeded the limit of 16777216 characters or 100000 text(), image(), and console calls. " +
					"Print a summary instead, or write large data to a file with a tool.",
			);
			done(false, describeError(error));
			throw error;
		}
		bridge("output", kind, data, mimeType);
	}

	/** 【CodingAgent】【脚本沙箱】把脚本值转换成文本。 @param {*} value 脚本值 @returns {*} 操作结果 */

	function outputText(value) {
		if (value === undefined || value === null || typeof value !== "object" && typeof value !== "function") {
			return String(value);
		}
		const json = stringify(value);
		return json === undefined ? String(value) : json;
	}

	/** 【CodingAgent】【脚本沙箱】追加文本输出。 @param {*} value 脚本值 @returns {*} 操作结果 */

	function text(value) {
		let rendered;
		try {
			rendered = outputText(value);
		} catch (error) {
			throw new TypeErrorCtor(error instanceof ErrorCtor ? error.message : String(error));
		}
		output("text", rendered);
	}

	/** 【CodingAgent】【脚本沙箱】读取图像块或数据 URI。 @param {*} value 脚本值 @returns {*} 操作结果 */

	function imageUrl(value) {
		if (typeof value === "string") return value;
		if (typeof value !== "object" || value === null || Array.isArray(value)) {
			throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
		}
		if (value.image_url !== undefined) {
			if (typeof value.image_url !== "string") throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
			return value.image_url;
		}
		if (typeof value.type !== "string") throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
		if (value.type !== "image") {
			throw new TypeErrorCtor('image only accepts MCP image blocks, got "' + value.type + '"');
		}
		if (typeof value.data !== "string" || value.data === "") throw new TypeErrorCtor("image expected MCP image data");
		if (value.data.toLowerCase().startsWith("data:")) return value.data;
		return "data:;base64," + value.data;
	}

	const IMAGE_SIGNATURES = [
		["image/png", /^iVBORw0KGg/],
		["image/jpeg", /^[/]9j[/](?!9)/],
		["image/gif", /^R0lGOD[dl]h/],
		["image/webp", /^UklG.{8}RUJQ/],
	];

	/** 【CodingAgent】【脚本沙箱】校验 Base64 和图像格式并追加图像。 @param {*} value 脚本值 @returns {*} 操作结果 */

	function image(value) {
		const url = imageUrl(value);
		if (url === "") throw new TypeErrorCtor("image expects a non-empty image URL string, an object with image_url, or a raw MCP image block");
		const colon = url.indexOf(":");
		const scheme = colon === -1 ? "" : url.slice(0, colon).toLowerCase();
		if (scheme === "http" || scheme === "https") {
			throw new TypeErrorCtor("remote image URLs are not supported in tool outputs. Pass a base64 data URI instead");
		}
		const comma = url.indexOf(",");
		const header = comma === -1 ? [] : url.slice(colon + 1, comma).split(";");
		if (scheme !== "data" || comma === -1 || header.slice(1).every((part) => part.toLowerCase() !== "base64")) {
			throw new TypeErrorCtor("invalid image output. Pass a base64 data URI instead");
		}
		const data = url.slice(comma + 1).replace(/\s+/g, "");
		if (data.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(data)) {
			throw new TypeErrorCtor("invalid image output. The image data is not valid base64 (truncated or corrupted?)");
		}
		const head = data.slice(0, 16);
		const signature = IMAGE_SIGNATURES.find(([, pattern]) => pattern.test(head));
		if (!signature) {
			throw new TypeErrorCtor("invalid image output. The image data is not a PNG, JPEG, GIF, or WebP image");
		}
		output("image", data, signature[0]);
	}

	/** 【CodingAgent】【脚本沙箱】保留成功状态并立即结束脚本。 @returns {*} 操作结果 */

	function exit() {
		let writesJson;
		try {
			writesJson = serializeWrites();
		} catch (error) {
			done(false, describeError(error));
			throw EXIT;
		}
		done(true, undefined, writesJson);
		throw EXIT;
	}

	const console = {};
	for (const level of ["log", "info", "warn", "error", "debug"]) {
		console[level] = (...args) => {
			output("text", args.map(format).join(" "));
		};
	}
	Object.freeze(console);

	Object.defineProperty(globalThis, "tools", { value: toolsProxy, enumerable: true });
	Object.defineProperty(globalThis, "ALL_TOOLS", { value: allTools, enumerable: true });
	Object.defineProperty(globalThis, "console", { value: console, enumerable: true });
	Object.defineProperty(globalThis, "text", { value: text, enumerable: true });
	Object.defineProperty(globalThis, "image", { value: image, enumerable: true });
	Object.defineProperty(globalThis, "exit", { value: exit, enumerable: true });

	return {
		/** 【CodingAgent】【脚本沙箱】完成一次宿主调用的 Promise。 @param {*} id 请求标识 @param {*} ok 成功状态 @param {*} payload 结果或错误 @returns {*} 操作结果 */
		settle(id, ok, payload) {
			const entry = pending.get(id);
			if (!entry) return;
			pending.delete(id);
			if (!ok) {
				entry.reject(new ErrorCtor(payload));
				return;
			}
			let value;
			try {
				value = payload === undefined ? undefined : parse(payload);
			} catch (error) {
				entry.reject(error);
				return;
			}
			entry.resolve(value);
		},
		/** 【CodingAgent】【脚本沙箱】执行脚本并交付完成状态。 @param {*} fn 脚本函数 @returns {*} 操作结果 */
		run(fn) {
			let promise;
			try {
				promise = fn(toolsProxy, console);
			} catch (error) {
				done(false, describeError(error));
				return;
			}
			promiseThen.call(
				promise,
				(value) => {
					let json;
					try {
						json = serialize(value);
					} catch (error) {
						done(false, describeError(error));
						return;
					}
					done(true, json, serializeWrites());
				},
				(error) => {
					done(false, describeError(error));
				},
			);
		},
		/** 【CodingAgent】【脚本沙箱】检测永远无法完成的 Promise。 @returns {*} 操作结果 */
		stalled() {
			if (finished || pending.size > 0) return false;
			done(
				false,
				stringify({
					name: "Error",
					message:
						"The script is waiting on a promise that can never settle: no tool call is pending, and timers do not exist here.",
				}),
			);
			return true;
		},
	};
});
