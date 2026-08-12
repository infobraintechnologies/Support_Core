// XSS regression test for notification, toast, and error rendering.
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const apiRoot = path.resolve(__dirname, "..", "..", "..", "CBSSupport.API");

const PAYLOADS = [
    "<img src=x onerror=globalThis.__xssTriggered=true>",
    "<script>globalThis.__xssTriggered=true</script>",
    "<div onclick=globalThis.__xssTriggered=true>",
    "&lt;script&gt;globalThis.__xssTriggered=true&lt;/script&gt;",
    "plain text with <strong>tags</strong> and \"quotes\""
];


class ClassListStub {
    constructor() { this._classes = new Set(); }
    add(...names) { names.forEach(name => this._classes.add(name)); }
    remove(...names) { names.forEach(name => this._classes.delete(name)); }
    contains(name) { return this._classes.has(name); }
    toggle(name, force) {
        if (force === undefined) {
            if (this._classes.has(name)) { this._classes.delete(name); return false; }
            this._classes.add(name); return true;
        }
        if (force) this._classes.add(name); else this._classes.delete(name);
        return Boolean(force);
    }
    toString() { return Array.from(this._classes).join(" "); }
}

class ElementStub {
    constructor(tagName, id) {
        this.tagName = String(tagName || "div").toUpperCase();
        this._children = [];
        this.attributes = new Map();
        this.dataset = {};
        this.style = {};
        this.classList = new ClassListStub();
        this._listeners = new Map();
        this.className = "";
        this.id = id || "";
        this.hidden = false;
        this.disabled = false;
        this._text = "";
        this._innerHTML = "";
        this.parentNode = null;
    }

    set textContent(value) {
        this._text = String(value ?? "");
        this._children = [];
        this._innerHTML = "";
    }
    get textContent() { return this._text; }

    set innerHTML(value) {
        this._innerHTML = String(value ?? "");
        this._children = [];
        this._text = "";
    }
    get innerHTML() { return this._innerHTML; }

    get children() { return this._children; }
    get firstElementChild() { return this._children[0] || null; }
    get lastElementChild() { return this._children[this._children.length - 1] || null; }

    appendChild(child) {
        child.parentNode = this;
        this._children.push(child);
        return child;
    }
    append(...nodes) { nodes.forEach(node => this.appendChild(node)); }
    replaceChildren(...nodes) {
        this._children = [];
        this._text = "";
        nodes.forEach(node => this.appendChild(node));
    }
    remove() {
        if (this.parentNode) {
            const index = this.parentNode._children.indexOf(this);
            if (index >= 0) this.parentNode._children.splice(index, 1);
            this.parentNode = null;
        }
    }
    replaceWith(...nodes) {
        const parent = this.parentNode;
        if (!parent) return;
        const index = parent._children.indexOf(this);
        if (index >= 0) parent._children.splice(index, 1, ...nodes);
    }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name); }
    addEventListener(type, fn) {
        if (!this._listeners.has(type)) this._listeners.set(type, []);
        this._listeners.get(type).push(fn);
    }
    dispatch(type) { (this._listeners.get(type) || []).forEach(fn => fn({ type })); }
    closest() { return null; }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0 }; }
    focus() {}
    click() {}
}

const documentStub = {
    createElement: tag => new ElementStub(tag),
    createTextNode: text => ({ nodeType: 3, textContent: String(text) }),
    querySelector: () => null,
    getElementById: () => null,
    body: new ElementStub("body")
};

const bootstrapStub = {
    Toast: class ToastStub {
        constructor(element, options) {
            this.element = element;
            this.options = options || {};
        }
        show() {}
    }
};

const localStorageStub = {
    _store: new Map(),
    getItem(key) { return this._store.has(key) ? this._store.get(key) : null; },
    setItem(key, value) { this._store.set(key, String(value)); },
    removeItem(key) { this._store.delete(key); }
};

globalThis.window = globalThis;
globalThis.document = documentStub;
globalThis.bootstrap = bootstrapStub;
globalThis.localStorage = localStorageStub;
globalThis.fetch = async () => ({ ok: true, json: async () => [] });

function loadScript(relativePath) {
    const absolute = path.join(apiRoot, relativePath);
    const source = fs.readFileSync(absolute, "utf8");
    vm.runInThisContext(source, { filename: relativePath });
}

function allDescendants(root) {
    const result = [];
    const visit = node => {
        (node._children || []).forEach(child => { result.push(child); visit(child); });
    };
    visit(root);
    return result;
}

function findByClass(root, className) {
    return allDescendants(root).find(node =>
        String(node.className || "").split(/\s+/).includes(className));
}

function hasElement(root, tagName) {
    return allDescendants(root).some(node => node.tagName === tagName.toUpperCase());
}

function collectInnerHtml(root) {
    return allDescendants(root)
        .map(node => node.innerHTML)
        .filter(value => value && value.length > 0)
        .join("\n");
}


globalThis.__xssTriggered = false;

loadScript("wwwroot/js/admin/admin-utils.js");
loadScript("wwwroot/js/admin/admin-notification.js");

assert.equal(typeof globalThis.AdminUtils, "object", "AdminUtils should load");
assert.equal(typeof globalThis.AdminNotifications, "object", "AdminNotifications should load");


for (const payload of PAYLOADS) {
    globalThis.AdminUtils.showNotification(payload, "error");

    const container = documentStub.body.children[documentStub.body.children.length - 1];
    assert.ok(container, "toast container should be attached");
    assert.equal(container.className, "toast-container", "toast container formatting preserved");

    const toasts = allDescendants(container).filter(node => node.tagName === "DIV")
        .filter(node => String(node.className).split(/\s+/).includes("toast"));
    assert.equal(toasts.length, 1, "exactly one toast rendered per call");

    const toast = toasts[0];
    assert.ok(String(toast.className).startsWith("toast"), "toast formatting preserved");

    const body = findByClass(toast, "toast-body");
    assert.ok(body, "toast body present");
    assert.equal(body.textContent, payload, "payload must be displayed as text, verbatim");

    const close = findByClass(toast, "btn-close");
    assert.ok(close, "close button present");
    assert.equal(close.getAttribute("data-bs-dismiss"), "toast", "close button wired for Bootstrap dismiss");

    assert.equal(hasElement(toast, "img"), false, `no img element from payload: ${payload}`);
    assert.equal(hasElement(toast, "script"), false, `no script element from payload: ${payload}`);
    assert.equal(globalThis.__xssTriggered, false, "payload must not execute");

    assert.equal(
        collectInnerHtml(toast).includes(payload),
        false,
        "payload must never be injected through innerHTML");
}


const container = documentStub.createElement("div");
container.id = "admin-notification-list";
documentStub.getElementById = id =>
    id === "admin-notification-list" ? container : null;

const unread = PAYLOADS.slice(0, 4).map((payload, index) => ({
    id: 1000 + index,
    caseId: 2000 + index,
    eventType: "CaseReplyCreated",
    title: `<b>sender ${index}</b>`,
    message: payload,
    createdAt: "2026-08-04T10:00:00Z",
    readAt: null
}));

globalThis.fetch = async () => ({ ok: true, json: async () => ({ items: unread, unreadCount: unread.length }) });

await globalThis.AdminNotifications.loadNotifications();

const messages = allDescendants(container)
    .filter(node => String(node.className || "").split(/\s+/).includes("notification-message"));
const titles = allDescendants(container)
    .filter(node => String(node.className || "").split(/\s+/).includes("notification-title"));
assert.equal(messages.length, unread.length, "one notification message rendered per unread instruction");
assert.equal(titles.length, unread.length, "one notification title rendered per notification");

for (let i = 0; i < unread.length; i += 1) {
    const message = messages[i];
    assert.ok(
        message.textContent.includes(unread[i].message),
        `payload ${i} displayed as text in notification list`);
    assert.ok(
        titles[i].textContent.includes(unread[i].title),
        `title ${i} displayed as text in notification list`);
}

assert.equal(hasElement(container, "img"), false, "no img element from notification payloads");
assert.equal(hasElement(container, "script"), false, "no script element from notification payloads");
assert.equal(globalThis.__xssTriggered, false, "notification payloads must not execute");

assert.equal(
    collectInnerHtml(container).includes(unread[0].message),
    false,
    "notification payload must never be injected through innerHTML");


globalThis.fetch = async () => ({ ok: true, json: async () => ({ items: [], unreadCount: 0 }) });
await globalThis.AdminNotifications.loadNotifications();
assert.equal(
    findByClass(container, "notification-empty") !== null,
    true,
    "empty notification state still renders");

console.log("PASS: all notification/toast XSS regression checks succeeded.");
