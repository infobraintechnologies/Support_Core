"use strict";

window.AdminChat = (() => {
    const conversationById = new Map();
    let mainChatContext = null;
    let lastMainChatMessageDate = null;
    let typingTimer = null;
    let initialized = false;
    let listRequest = 0;
    let openRequest = 0;
    let isSending = false;
    const floatingAttachmentComposers = new WeakMap();

    const floatingChatContainer = document.getElementById("floating-chat-container");
    const mainChatPanelBody = document.getElementById("chat-panel-body");
    const mainMessageInput = document.getElementById("message-input");
    const mainSendButton = document.getElementById("send-button");
    const attachmentsEnabled = document.body.dataset.attachmentsEnabled === "true";
    const attachmentButton = document.getElementById("admin-attachment-button");
    const attachmentInput = document.getElementById("admin-attachment-file-input");
    if (attachmentButton) attachmentButton.hidden = true;
    if (attachmentInput) attachmentInput.disabled = true;
    const attachmentComposer = attachmentsEnabled
        ? window.CBSSupportAttachments?.createComposer({
            input: document.getElementById("admin-attachment-file-input"),
            button: attachmentButton,
            list: document.getElementById("admin-attachment-upload-list"),
            getConversationId: () => mainChatContext?.id,
            onReadyChanged: () => updateSendButtonState(),
            onError: message => setStatus("admin-chat-send-status", message, "is-error")
        })
        : null;

    function messaging() {
        return window.AdminSignalR?.getMessaging?.();
    }

    function isNearMainChatBottom() {
        if (!mainChatPanelBody) return true;
        return mainChatPanelBody.scrollHeight
            - mainChatPanelBody.scrollTop
            - mainChatPanelBody.clientHeight < 80;
    }

    function setText(element, value) {
        if (element) element.textContent = value == null ? "" : String(value);
    }

    function setStatus(id, text, kind = "") {
        const element = document.getElementById(id);
        if (!element) return;
        element.textContent = text || "";
        element.className = id === "admin-chat-connection-status"
            ? `admin-chat-status ${kind}`.trim()
            : `admin-chat-send-status ${kind}`.trim();
    }

    function parseProblem(response, body) {
        const error = new Error(
            body?.detail || body?.title || `Request failed (${response.status}).`);
        error.status = response.status;
        error.problem = body;
        return error;
    }

    async function fetchJson(url, options = {}) {
        const response = await fetch(url, {
            credentials: "same-origin",
            ...options
        });
        const contentType = response.headers.get("content-type") || "";
        const body = contentType.includes("json")
            ? await response.json()
            : null;
        if (!response.ok) throw parseProblem(response, body);
        return body;
    }

    async function listV2Conversations() {
        const client = messaging();
        if (typeof client?.listConversations === "function") {
            const page = await client.listConversations({ limit: 100 });
            return Array.isArray(page) ? page : (page?.items || []);
        }
        const page = await fetchJson("/api/v1/conversations?limit=100");
        return Array.isArray(page) ? page : (page?.items || []);
    }

    async function loadInternalChats() {
        const data = await fetchJson("/v1/api/instructions/by-type/internal-team-chat");
        const seen = new Set();
        const internalChats = (Array.isArray(data) ? data : [])
            .filter(item => {
                const id = Number(item.instructionId || item.id);
                if (!Number.isSafeInteger(id) || id <= 0 || seen.has(id)) return false;
                seen.add(id);
                return true;
            })
            .map(item => ({
                conversationId: item.instructionId || item.id,
                displayName: "Internal Discussion",
                subtitle: item.instruction || "No recent messages",
                route: "internal-team-chat"
            }));
        return {
            internalChats,
            ticketChats: [],
            inquiryChats: []
        };
    }

    async function initializeChatsPage(currentClientId) {
        const requestId = ++listRequest;
        setListsLoading();
        setConnectionStatus("loading");

        const [v2Result, internalResult] = await Promise.allSettled([
            listV2Conversations(),
            loadInternalChats()
        ]);
        if (requestId !== listRequest) return;

        const conversations = v2Result.status === "fulfilled" ? v2Result.value : [];
        const internal = internalResult.status === "fulfilled"
            ? internalResult.value
            : { internalChats: [], ticketChats: [], inquiryChats: [] };

        if (v2Result.status === "rejected") {
            console.error("AdminChat: failed to load Messaging V2 conversations.", v2Result.reason);
            showListError("group-chats", "Could not load group chats.");
            showListError("private-chats", "Could not load private chats.");
        } else {
            renderV2Lists(conversations, currentClientId);
        }

        if (internalResult.status === "rejected") {
            console.error("AdminChat: failed to load internal conversations.", internalResult.reason);
            showListError("internal-chats", "Could not load internal conversations.");
            renderLegacyLists({ internalChats: [], ticketChats: [], inquiryChats: [] }, true);
        } else {
            renderLegacyLists(internal, true);
        }
        restoreActiveListState();
        applyConversationFilters();
        setConnectionStatus();
    }

    function restoreActiveListState() {
        if (!mainChatContext?.id) return;
        const item = document.querySelector(
            `.admin-conversation-item[data-id="${mainChatContext.id}"]`);
        if (item) item.classList.add("active");
        const latest = conversationById.get(mainChatContext.id);
        if (latest) {
            mainChatContext.version = Number(latest.version || mainChatContext.version);
            mainChatContext.latestSequence = Number(
                latest.latestSequence || mainChatContext.latestSequence);
        }
    }

    function setListsLoading() {
        ["group-chats", "private-chats", "internal-chats", "ticket-chats", "inquiry-chats"]
            .forEach(id => {
                const container = document.getElementById(id);
                if (!container) return;
                container.replaceChildren(createListMessage("Loading…", true));
            });
    }

    function createListMessage(message, loading = false, danger = false) {
        const element = document.createElement("div");
        element.className = `admin-chat-loading${danger ? " text-danger" : ""}`;
        if (loading) {
            const spinner = document.createElement("span");
            spinner.className = "spinner-border spinner-border-sm me-2";
            spinner.setAttribute("aria-hidden", "true");
            element.appendChild(spinner);
        }
        element.appendChild(document.createTextNode(message));
        return element;
    }

    function showListError(id, message) {
        document.getElementById(id)?.replaceChildren(createListMessage(message, false, true));
    }

    function renderV2Lists(summaries, selectedClientId) {
        conversationById.clear();
        const active = summaries.filter(summary =>
            summary && String(summary.state).toLowerCase() === "active");
        for (const summary of active) conversationById.set(Number(summary.id), summary);

        const groups = active.filter(summary =>
            String(summary.kind).toLowerCase() === "group"
            && selectedClientId
            && String(summary.clientId) === String(selectedClientId));
        const privateChats = active.filter(summary =>
            String(summary.kind).toLowerCase() === "private");

        const groupContainer = document.getElementById("group-chats");
        if (groupContainer) {
            groupContainer.replaceChildren();
            if (!selectedClientId) {
                groupContainer.appendChild(createListMessage("Choose a tenant to view its group chat."));
            } else if (groups.length) {
                groups.forEach(summary =>
                    groupContainer.appendChild(createV2ConversationItem(summary)));
            } else {
                const start = document.createElement("button");
                start.type = "button";
                start.className = "admin-conversation-item admin-conversation-create";
                start.dataset.createGroup = "true";
                start.dataset.type = "group";
                start.dataset.searchText = "start group chat";
                start.innerHTML = '<i class="fas fa-users me-2" aria-hidden="true"></i>';
                start.appendChild(document.createTextNode("Start group chat"));
                groupContainer.appendChild(start);
            }
        }

        const privateContainer = document.getElementById("private-chats");
        if (privateContainer) {
            privateContainer.replaceChildren();
            if (!privateChats.length) {
                privateContainer.appendChild(createListMessage("No assigned private chats."));
            } else {
                privateChats
                    .sort((left, right) =>
                        Number(right.unreadCount || 0) - Number(left.unreadCount || 0)
                        || Number(right.id) - Number(left.id))
                    .forEach(summary =>
                        privateContainer.appendChild(createV2ConversationItem(summary)));
            }
        }

        for (const kind of ["ticket", "inquiry"]) {
            const container = document.getElementById(`${kind}-chats`);
            if (!container) continue;
            const cases = active.filter(summary =>
                String(summary.kind).toLowerCase() === kind
                && selectedClientId
                && String(summary.clientId) === String(selectedClientId));
            container.replaceChildren();
            if (!selectedClientId) {
                container.appendChild(createListMessage("Choose a tenant to view this section."));
            } else if (!cases.length) {
                container.appendChild(createListMessage(`No ${kind} chats.`));
            } else {
                cases.forEach(summary =>
                    container.appendChild(createV2ConversationItem(summary)));
            }
        }
    }

    function createV2ConversationItem(summary) {
        const kind = String(summary.kind || "").toLowerCase();
        const tenantName = getTenantName(summary.clientId);
        const title = kind === "private"
            ? (summary.clientDisplayName || `Client user ${summary.clientUserId}`)
            : kind === "group"
                ? `${tenantName} group`
                : `${capitalize(kind)} #${summary.id}`;
        const subtitle = kind === "private"
            ? `Private · ${tenantName}`
            : kind === "group"
                ? "Group conversation"
                : `${tenantName} · ${capitalize(kind)} conversation`;

        const button = createConversationButton({
            id: summary.id,
            type: kind,
            name: title,
            route: "messaging-v2",
            subtitle
        });
        button.dataset.version = String(summary.version);
        button.dataset.latestSequence = String(summary.latestSequence || 0);
        renderUnreadBadge(button, Number(summary.unreadCount || 0));
        return button;
    }

    function getTenantName(clientId) {
        const option = Array.from(
            document.getElementById("client-switcher-chats")?.options || [])
            .find(candidate => String(candidate.value) === String(clientId));
        return option?.textContent?.trim() || `Tenant ${clientId}`;
    }

    function renderLegacyLists(data, hasTenant) {
        renderLegacySection("internal-chats", data.internalChats, "internal", hasTenant);
    }

    function renderLegacySection(id, items, type, hasTenant) {
        const container = document.getElementById(id);
        if (!container) return;
        container.replaceChildren();
        if (!hasTenant) {
            container.appendChild(createListMessage("Choose a tenant to view this section."));
            return;
        }
        if (!items.length) {
            container.appendChild(createListMessage(`No ${type} chats.`));
            return;
        }
        items.forEach(item => container.appendChild(createLegacyConversationItem(item, type)));
    }

    function createLegacyConversationItem(item, type) {
        return createConversationButton({
            id: item.conversationId,
            name: item.displayName || `${type} conversation`,
            subtitle: item.subtitle || "No recent messages",
            route: type === "ticket" || type === "inquiry"
                ? "messaging-v2"
                : (item.route || ""),
            type
        });
    }

    function createConversationButton(data) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "admin-conversation-item";
        button.dataset.id = String(data.id);
        button.dataset.name = String(data.name || "Conversation");
        button.dataset.type = String(data.type || "");
        button.dataset.route = String(data.route || "");
        button.dataset.searchText = `${data.name || ""} ${data.subtitle || ""}`.toLocaleLowerCase();

        const row = document.createElement("span");
        row.className = "d-flex w-100 align-items-center";
        const avatar = document.createElement("span");
        avatar.className = `admin-avatar-initials ${getAvatarClass(data.type)} me-3`;
        avatar.setAttribute("aria-hidden", "true");
        const icon = document.createElement("i");
        icon.className = `fas ${getAvatarIconClass(data.type)}`;
        avatar.appendChild(icon);

        const copy = document.createElement("span");
        copy.className = "flex-grow-1 admin-conversation-copy";
        const title = document.createElement("span");
        title.className = "admin-conversation-title";
        title.textContent = data.name || "Conversation";
        const subtitle = document.createElement("small");
        subtitle.className = "admin-conversation-subtitle";
        subtitle.textContent = data.subtitle || "No recent messages";
        copy.append(title, subtitle);

        const badge = document.createElement("span");
        badge.className = "admin-unread-badge";
        badge.hidden = true;
        badge.setAttribute("aria-label", "Unread messages");
        row.append(avatar, copy, badge);
        button.appendChild(row);
        return button;
    }

    function getAvatarClass(type) {
        return {
            private: "admin-avatar-bg-purple",
            internal: "admin-avatar-bg-blue",
            ticket: "admin-avatar-bg-orange",
            inquiry: "admin-avatar-bg-cyan",
            group: "admin-avatar-bg-success"
        }[type] || "admin-avatar-bg-secondary";
    }

    function getAvatarIconClass(type) {
        return {
            private: "fa-user",
            internal: "fa-building",
            ticket: "fa-ticket-alt",
            inquiry: "fa-question-circle",
            group: "fa-users"
        }[type] || "fa-comment";
    }

    function renderUnreadBadge(item, count) {
        const badge = item?.querySelector(".admin-unread-badge");
        if (!badge) return;
        const safeCount = Math.max(0, Number(count) || 0);
        badge.hidden = safeCount === 0;
        badge.textContent = safeCount > 99 ? "99+" : String(safeCount);
        item.classList.toggle("has-unread", safeCount > 0);
    }

    function applyConversationFilters() {
        const query = String(
            document.getElementById("admin-conversation-search")?.value || "")
            .trim()
            .toLocaleLowerCase();
        const filter = String(
            document.getElementById("admin-conversation-filter")?.value || "all")
            .toLocaleLowerCase();
        for (const item of document.querySelectorAll(".admin-conversation-item")) {
            const type = String(item.dataset.type || "").toLocaleLowerCase();
            const matchesType = filter === "all"
                || type === filter
                || (filter === "other" && type !== "group" && type !== "private");
            const matchesText = !query
                || String(item.dataset.searchText || "").includes(query);
            item.hidden = !(matchesType && matchesText);
        }
    }

    function initializeChatSidebar() {
        if (initialized) return;
        initialized = true;

        document.addEventListener("click", handleDocumentClick);
        document.getElementById("refresh-conversations-btn")
            ?.addEventListener("click", () => refreshAdminConversations(
                window.AdminCore?.getCurrentClientId?.()));
        document.getElementById("admin-chat-back-btn")
            ?.addEventListener("click", showMobileList);
        document.getElementById("admin-conversation-search")
            ?.addEventListener("input", applyConversationFilters);
        document.getElementById("admin-conversation-filter")
            ?.addEventListener("change", applyConversationFilters);
        mainSendButton?.addEventListener("click", sendMainChatMessage);
        mainMessageInput?.addEventListener("input", handleMessageInput);
        mainMessageInput?.addEventListener("keydown", event => {
            if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                sendMainChatMessage();
            }
        });
        initializeNewPrivateChat();
        initializeLifecycleControls();
        updateSendButtonState();
    }

    function handleDocumentClick(event) {
        const navigation = event.target.closest(".admin-sidebar .nav-link[data-page]");
        if (navigation && navigation.dataset.page !== "chats") {
            void attachmentComposer?.resetForConversation();
        }

        const toggle = event.target.closest(".admin-chat-section-toggle");
        if (toggle) {
            event.stopPropagation();
            const content = document.getElementById(toggle.dataset.target);
            const collapsed = content?.classList.toggle("collapsed") || false;
            toggle.classList.toggle("expanded", !collapsed);
            toggle.setAttribute(
                "aria-expanded",
                String(!collapsed));
            return;
        }

        const createGroup = event.target.closest("[data-create-group]");
        if (createGroup) {
            createAndOpenGroup(createGroup);
            return;
        }

        const item = event.target.closest(".admin-conversation-item[data-id]");
        if (item) openConversationItem(item);
    }

    async function createAndOpenGroup(button) {
        const clientId = window.AdminCore?.getCurrentClientId?.();
        if (!clientId) {
            AdminUtils.showNotification("Choose a tenant first.", "error");
            return;
        }
        button.disabled = true;
        try {
            const conversation = await messaging()?.getOrCreateGroupForTenant(Number(clientId));
            await refreshAdminConversations(clientId);
            document.querySelector(
                `.admin-conversation-item[data-id="${Number(conversation.id)}"]`)?.click();
        } catch (error) {
            AdminUtils.showNotification(error.message || "Could not start group chat.", "error");
        } finally {
            button.disabled = false;
        }
    }

    async function openConversationItem(item) {
        const conversationId = Number(item.dataset.id);
        if (!Number.isSafeInteger(conversationId) || conversationId <= 0) {
            AdminUtils.showNotification("Invalid conversation.", "error");
            return;
        }
        if (conversationId === Number(mainChatContext?.id)) {
            showMobileDetail();
            return;
        }
        const requestId = ++openRequest;

        saveCurrentDraft();
        if (mainChatContext?.id) messaging()?.cancelReconcile?.(mainChatContext.id);
        await leaveCurrentConversation(conversationId);
        if (requestId !== openRequest) return;
        const summary = conversationById.get(conversationId);
        mainChatContext = {
            id: conversationId,
            name: item.dataset.name || "Conversation",
            route: item.dataset.route || "",
            type: item.dataset.type || "",
            version: Number(summary?.version || item.dataset.version || 0),
            latestSequence: Number(summary?.latestSequence || item.dataset.latestSequence || 0),
            isV2: item.dataset.route === "messaging-v2"
        };
        updateAttachmentAvailability();

        document.querySelectorAll(".admin-conversation-item.active")
            .forEach(element => element.classList.remove("active"));
        item.classList.add("active");
        renderUnreadBadge(item, 0);
        updateAdminChatHeader();
        showMobileDetail();

        if (mainMessageInput) {
            mainMessageInput.value = messaging()?.store.loadDraft(conversationId) || "";
        }
        updateSendButtonState();
        setStatus("admin-chat-send-status", "Opening conversation…");

        try {
            await window.AdminSignalR?.joinConversation(
                conversationId,
                mainChatContext.isV2);
            if (requestId !== openRequest) return;
            await loadAdminChatMessages(conversationId);
            if (requestId !== openRequest) return;
            await markActiveConversationRead();
            setStatus("admin-chat-send-status", "");
        } catch (error) {
            if (requestId !== openRequest || error?.name === "AbortError") return;
            console.error("AdminChat: failed to open conversation.", error);
            renderMainPanelMessage("Could not open this conversation.", true);
            setStatus("admin-chat-send-status", "Conversation unavailable.", "is-error");
        }
    }

    function saveCurrentDraft() {
        if (mainChatContext?.id && mainMessageInput) {
            messaging()?.store.saveDraft(mainChatContext.id, mainMessageInput.value);
        }
    }

    async function leaveCurrentConversation(nextId = null) {
        if (!mainChatContext?.id || mainChatContext.id === nextId) return;
        attachmentComposer?.resetForConversation();
        try {
            await window.AdminSignalR?.leaveConversation(mainChatContext.id);
        } catch (error) {
            console.warn("AdminChat: leave failed.", error);
        }
    }

    function updateAdminChatHeader() {
        document.querySelector(".admin-chat-placeholder")?.remove();
        setText(document.querySelector(".admin-chat-title"), mainChatContext.name);
        setText(
            document.querySelector(".admin-chat-subtitle"),
            `${capitalize(mainChatContext.type)} chat`);
        const isPrivate = mainChatContext.isV2 && mainChatContext.type === "private";
        document.getElementById("chat-transfer-btn").style.display = isPrivate ? "" : "none";
        document.getElementById("chat-archive-btn").style.display = isPrivate ? "" : "none";
        document.getElementById("chat-info-btn").style.display = "";
        document.getElementById("chat-settings-btn").style.display = "";
    }

    function capitalize(value) {
        const text = String(value || "");
        return text ? text[0].toUpperCase() + text.slice(1) : "Conversation";
    }

    async function loadAdminChatMessages(conversationId) {
        renderMainPanelMessage("Loading messages…");
        const messages = mainChatContext?.isV2
            ? await messaging()?.reconcile(Number(conversationId))
            : await fetchJson(`/v1/api/instructions/messages/${Number(conversationId)}`);
        if (!Array.isArray(messages)) throw new Error("Invalid message response.");
        if (Number(mainChatContext?.id) !== Number(conversationId)) return;

        mainChatPanelBody?.replaceChildren();
        lastMainChatMessageDate = null;
        if (!messages.length) {
            renderMainPanelMessage("No messages yet. Start the conversation.");
            return;
        }
        messages.forEach(message => displayMainChatMessage(
            mainChatContext?.isV2 ? toLegacyMessage(message) : message,
            true));
        AdminUtils.scrollToBottom(mainChatPanelBody);
        const latest = mainChatContext?.isV2 ? messages.reduce(
            (maximum, message) => Math.max(maximum, Number(message.sequence || 0)),
            mainChatContext.latestSequence || 0) : 0;
        mainChatContext.latestSequence = latest;
    }

    function renderMainPanelMessage(message, danger = false) {
        if (!mainChatPanelBody) return;
        const element = document.createElement("div");
        element.className = `text-center p-4 ${danger ? "text-danger" : "text-muted"}`;
        element.textContent = message;
        mainChatPanelBody.replaceChildren(element);
    }

    function addMainChatDateSeparator(dateValue) {
        if (!mainChatPanelBody) return;
        const parsed = new Date(dateValue);
        const dateKey = Number.isNaN(parsed.getTime()) ? "unknown" : parsed.toDateString();
        if (lastMainChatMessageDate === dateKey) return;
        lastMainChatMessageDate = dateKey;
        const separator = document.createElement("div");
        separator.className = "date-separator";
        separator.textContent = AdminUtils.formatDateForSeparator(dateValue);
        mainChatPanelBody.appendChild(separator);
    }

    function displayMainChatMessage(message, isHistory = false) {
        if (!mainChatPanelBody) return;
        const shouldAutoScroll = !isHistory && isNearMainChatBottom();
        if (message.id
            && mainChatPanelBody.querySelector(`[data-message-id="${Number(message.id)}"]`)) {
            return;
        }

        const empty = mainChatPanelBody.querySelector(":scope > .text-center");
        empty?.remove();
        addMainChatDateSeparator(message.dateTime);

        const currentUser = window.AdminCore?.getCurrentUser?.();
        const isSent = String(message.insertUser) === String(currentUser?.id);
        const senderName = message.senderName
            || (isSent ? currentUser?.name || "Admin" : "Client");
        const senderId = message.insertUser || message.clientAuthUserId || "unknown";
        const lastGroup = mainChatPanelBody.lastElementChild;
        const canAppend = lastGroup?.classList.contains("message-group")
            && lastGroup.dataset.senderId === String(senderId);

        if (canAppend) {
            lastGroup.querySelector(".message-cluster")
                ?.appendChild(createMessageBubble(message));
        } else {
            const group = document.createElement("div");
            group.className = `message-group ${isSent ? "sent" : "received"}`;
            group.dataset.senderId = String(senderId);

            const cluster = document.createElement("div");
            cluster.className = "message-cluster";
            const sender = document.createElement("div");
            sender.className = "message-sender";
            sender.textContent = senderName;
            cluster.append(sender, createMessageBubble(message));

            const avatar = document.createElement("div");
            avatar.className = "chat-avatar";
            avatar.textContent = String(senderName).trim().charAt(0).toUpperCase() || "?";
            group.append(cluster, avatar);
            mainChatPanelBody.appendChild(group);
        }
        if (shouldAutoScroll) AdminUtils.scrollToBottom(mainChatPanelBody);
    }

    function createMessageBubble(message) {
        const bubble = document.createElement("div");
        bubble.className = "message-bubble";
        if (message.id) bubble.dataset.messageId = String(message.id);
        const text = document.createElement("p");
        text.className = "message-text";
        text.textContent = message.instruction || "";
        window.CBSSupportAttachments?.renderMessageAttachments(
            bubble,
            message.attachments || []);
        const timestamp = document.createElement("div");
        timestamp.className = "message-timestamp";
        timestamp.textContent = AdminUtils.formatTimestamp(message.dateTime);
        bubble.append(text, timestamp);
        return bubble;
    }

    function handleMessageInput() {
        if (mainChatContext?.id) {
            messaging()?.store.saveDraft(mainChatContext.id, mainMessageInput.value);
            window.AdminSignalR?.setTyping(mainChatContext.id, true)?.catch(() => {});
            clearTimeout(typingTimer);
            typingTimer = setTimeout(() => {
                window.AdminSignalR?.setTyping(mainChatContext?.id, false)?.catch(() => {});
            }, 1500);
        }
        updateSendButtonState();
    }

    function updateSendButtonState() {
        const canAttach = supportsAttachments(mainChatContext);
        const canSend = Boolean(
            mainChatContext?.id
            && !isSending
            && (mainMessageInput?.value.trim()
                || (canAttach && attachmentComposer?.getReadyIds().length > 0)));
        if (mainMessageInput) mainMessageInput.disabled = !mainChatContext?.id || isSending;
        if (attachmentButton) {
            attachmentButton.hidden = !canAttach;
            attachmentButton.disabled = !canAttach || isSending;
        }
        if (attachmentInput) attachmentInput.disabled = !canAttach || isSending;
        if (mainSendButton) mainSendButton.disabled = !canSend;
    }

    async function sendMainChatMessage() {
        const text = mainMessageInput?.value.trim() || "";
        const attachmentIds = supportsAttachments(mainChatContext)
            ? attachmentComposer?.getReadyIds() || []
            : [];
        if ((!text && attachmentIds.length === 0) || !mainChatContext?.id || isSending) return;
        isSending = true;
        updateSendButtonState();
        setStatus("admin-chat-send-status", "Sending…", "is-pending");
        try {
            const message = await window.AdminSignalR.sendMessage(
                mainChatContext.id,
                text,
                mainChatContext.isV2,
                attachmentIds);
            if (mainMessageInput.value.trim() === text) mainMessageInput.value = "";
            displayMainChatMessage(message);
            attachmentComposer?.clearBound(attachmentIds);
            mainChatContext.latestSequence = Math.max(
                mainChatContext.latestSequence || 0,
                Number(message.sequence || 0));
            setStatus("admin-chat-send-status", "Sent", "is-success");
            await markActiveConversationRead();
        } catch (error) {
            console.error("AdminChat: send failed.", error);
            setStatus("admin-chat-send-status", "Send failed. Your draft was kept.", "is-error");
            AdminUtils.showNotification("Message was not sent. Try again.", "error");
        } finally {
            isSending = false;
            updateSendButtonState();
        }
    }

    async function markActiveConversationRead() {
        if (!mainChatContext?.isV2 || !mainChatContext.latestSequence) return;
        try {
            if (typeof messaging()?.advanceRead === "function") {
                await messaging().advanceRead(
                    mainChatContext.id,
                    mainChatContext.latestSequence);
            } else {
                await fetchJson(
                    `/api/v1/conversations/${mainChatContext.id}/read`,
                    {
                        method: "PUT",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({
                            throughSequence: mainChatContext.latestSequence
                        })
                    });
            }
            const item = document.querySelector(
                `.admin-conversation-item[data-id="${mainChatContext.id}"]`);
            renderUnreadBadge(item, 0);
        } catch (error) {
            if (error.status !== 409) console.warn("AdminChat: read cursor failed.", error);
        }
    }

    function initializeNewPrivateChat() {
        const modal = document.getElementById("new-private-chat-modal");
        const form = document.getElementById("new-private-chat-form");
        const tenant = document.getElementById("new-private-client");
        const user = document.getElementById("new-private-user");
        const submit = document.getElementById("new-private-chat-submit");
        if (!modal || !form || !tenant || !user || !submit) return;

        modal.addEventListener("show.bs.modal", () => {
            copyTenantOptions(tenant);
            const currentTenant = window.AdminCore?.getCurrentClientId?.();
            tenant.value = currentTenant || "";
            user.replaceChildren(new Option("Choose a tenant first", ""));
            user.disabled = true;
            submit.disabled = true;
            setText(document.getElementById("new-private-chat-status"), "");
            if (tenant.value) loadPrivateUsers(tenant.value);
        });
        tenant.addEventListener("change", () => loadPrivateUsers(tenant.value));
        user.addEventListener("change", () => {
            submit.disabled = !user.value;
        });
        form.addEventListener("submit", createPrivateConversation);
    }

    function copyTenantOptions(target) {
        const source = document.getElementById("client-switcher-chats");
        target.replaceChildren(new Option("Choose a tenant", ""));
        Array.from(source?.options || []).forEach(option => {
            if (option.value) target.appendChild(new Option(option.textContent, option.value));
        });
    }

    async function loadPrivateUsers(clientId) {
        const user = document.getElementById("new-private-user");
        const submit = document.getElementById("new-private-chat-submit");
        const status = document.getElementById("new-private-chat-status");
        user.replaceChildren(new Option(clientId ? "Loading users…" : "Choose a tenant first", ""));
        user.disabled = true;
        submit.disabled = true;
        setText(status, "");
        if (!clientId) return;

        try {
            const client = messaging();
            const users = typeof client?.getAvailableClientUsers === "function"
                ? await client.getAvailableClientUsers(Number(clientId))
                : await fetchJson(
                    `/api/v1/admin/clients/${Number(clientId)}/conversation-users`);
            user.replaceChildren(new Option("Choose a client user", ""));
            (users || []).forEach(directoryUser => {
                user.appendChild(new Option(
                    directoryUser.displayName,
                    String(directoryUser.id)));
            });
            user.disabled = !(users || []).length;
            setText(status, (users || []).length ? "" : "No active client users are available.");
        } catch (error) {
            user.replaceChildren(new Option("Could not load users", ""));
            setText(status, error.message || "Could not load client users.");
            status.className = "small text-danger";
        }
    }

    async function createPrivateConversation(event) {
        event.preventDefault();
        const tenant = document.getElementById("new-private-client");
        const user = document.getElementById("new-private-user");
        const submit = document.getElementById("new-private-chat-submit");
        const status = document.getElementById("new-private-chat-status");
        const clientUserId = Number(user.value);
        if (!tenant.value || !Number.isSafeInteger(clientUserId) || clientUserId <= 0) return;

        submit.disabled = true;
        setText(status, "Creating private chat…");
        status.className = "small";
        try {
            const client = messaging();
            const conversation = typeof client?.getOrCreatePrivate === "function"
                ? await client.getOrCreatePrivate(clientUserId)
                : await fetchJson("/api/v1/conversations/private", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ counterpartyUserId: clientUserId })
                });
            bootstrap.Modal.getOrCreateInstance(
                document.getElementById("new-private-chat-modal")).hide();
            await refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
            document.querySelector(
                `.admin-conversation-item[data-id="${Number(conversation.id)}"]`)?.click();
        } catch (error) {
            setText(status, error.message || "Could not create private chat.");
            status.className = "small text-danger";
            submit.disabled = false;
        }
    }

    function initializeLifecycleControls() {
        document.getElementById("chat-transfer-btn")?.addEventListener("click", () => {
            if (!mainChatContext?.id) return;
            document.getElementById("transfer-private-chat-form")?.reset();
            setText(document.getElementById("transfer-private-chat-status"), "");
            loadTransferAdmins();
            bootstrap.Modal.getOrCreateInstance(
                document.getElementById("transfer-private-chat-modal")).show();
        });
        document.getElementById("transfer-private-chat-form")
            ?.addEventListener("submit", transferPrivateConversation);
        document.getElementById("chat-archive-btn")
            ?.addEventListener("click", archivePrivateConversation);
    }

    async function loadTransferAdmins() {
        const select = document.getElementById("transfer-admin-user-id");
        const status = document.getElementById("transfer-private-chat-status");
        if (!select) return;
        select.disabled = true;
        select.replaceChildren(new Option("Loading administrators…", ""));
        try {
            const admins = await messaging()?.getAvailableAdmins();
            const currentAdminId = Number(window.AdminCore?.getCurrentUser?.()?.id);
            const choices = (admins || []).filter(admin =>
                Number(admin.id) !== currentAdminId);
            select.replaceChildren(new Option("Choose an administrator", ""));
            for (const admin of choices) {
                select.appendChild(new Option(
                    admin.displayName || `Administrator ${admin.id}`,
                    String(admin.id)));
            }
            select.disabled = choices.length === 0;
            if (!choices.length) {
                setText(status, "No other active administrators are available.");
            }
        } catch (error) {
            select.replaceChildren(new Option("Could not load administrators", ""));
            setText(status, error.message || "Could not load administrators.");
            status.className = "small mt-2 text-danger";
        }
    }

    async function transferPrivateConversation(event) {
        event.preventDefault();
        if (!mainChatContext?.id || !mainChatContext.version) return;
        const adminId = Number(document.getElementById("transfer-admin-user-id").value);
        const reason = document.getElementById("transfer-reason").value.trim();
        const submit = document.getElementById("transfer-private-chat-submit");
        const status = document.getElementById("transfer-private-chat-status");
        if (!Number.isSafeInteger(adminId) || adminId <= 0) return;
        if (!window.confirm("Transfer this private conversation now?")) return;

        submit.disabled = true;
        setText(status, "Transferring…");
        try {
            const updated = typeof messaging()?.transfer === "function"
                ? await messaging().transfer(mainChatContext.id, {
                    adminUserId: adminId,
                    expectedVersion: mainChatContext.version,
                    reason: reason || null
                })
                : await fetchJson(
                    `/api/v1/conversations/${mainChatContext.id}/assignment`,
                    {
                        method: "PUT",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({
                            adminUserId: adminId,
                            expectedVersion: mainChatContext.version,
                            reason: reason || null
                        })
                    });
            mainChatContext.version = Number(updated.version);
            bootstrap.Modal.getOrCreateInstance(
                document.getElementById("transfer-private-chat-modal")).hide();
            await disableAndRefreshActive("Conversation transferred.");
        } catch (error) {
            if (error.status === 409) {
                setText(status, "This conversation changed. Refreshing the latest version…");
                await refreshAfterConflict();
            } else {
                setText(status, error.message || "Transfer failed.");
            }
            status.className = "small mt-2 text-danger";
        } finally {
            submit.disabled = false;
        }
    }

    async function archivePrivateConversation() {
        if (!mainChatContext?.id || !mainChatContext.version) return;
        if (!window.confirm("Archive this private conversation? You will leave it immediately.")) {
            return;
        }
        const button = document.getElementById("chat-archive-btn");
        button.disabled = true;
        try {
            if (typeof messaging()?.archive === "function") {
                await messaging().archive(mainChatContext.id, mainChatContext.version);
            } else {
                await fetchJson(
                    `/api/v1/conversations/${mainChatContext.id}/archive`,
                    {
                        method: "PUT",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ expectedVersion: mainChatContext.version })
                    });
            }
            await disableAndRefreshActive("Conversation archived.");
        } catch (error) {
            if (error.status === 409) {
                AdminUtils.showNotification(
                    "This conversation changed. The latest list has been loaded.",
                    "error");
                await refreshAfterConflict();
            } else {
                AdminUtils.showNotification(error.message || "Archive failed.", "error");
            }
        } finally {
            button.disabled = false;
        }
    }

    async function disableAndRefreshActive(message) {
        const oldId = mainChatContext?.id;
        openRequest += 1;
        if (oldId) messaging()?.cancelReconcile?.(oldId);
        mainChatContext = null;
        isSending = false;
        updateSendButtonState();
        renderMainPanelMessage(message);
        setText(document.querySelector(".admin-chat-title"), "Select a Conversation");
        setText(document.querySelector(".admin-chat-subtitle"), "Choose from the list to continue");
        ["chat-transfer-btn", "chat-archive-btn", "chat-info-btn", "chat-settings-btn"]
            .forEach(id => {
                const control = document.getElementById(id);
                if (control) control.style.display = "none";
            });
        if (oldId) {
            try {
                await window.AdminSignalR?.leaveConversation(oldId);
            } catch (error) {
                console.warn("AdminChat: leave after lifecycle change failed.", error);
            }
        }
        showMobileList();
        await refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
    }

    async function refreshAfterConflict() {
        const activeId = mainChatContext?.id;
        await refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
        const latest = conversationById.get(activeId);
        if (latest && mainChatContext?.id === activeId) {
            mainChatContext.version = Number(latest.version);
            mainChatContext.latestSequence = Number(latest.latestSequence || 0);
            document.querySelector(
                `.admin-conversation-item[data-id="${activeId}"]`)?.classList.add("active");
        }
    }

    function handleIncomingMessage(message) {
        const conversationId = Number(message.instructionId);
        const item = document.querySelector(
            `.admin-conversation-item[data-id="${conversationId}"]`);
        if (mainChatContext?.id === conversationId) {
            displayMainChatMessage(message);
            mainChatContext.latestSequence = Math.max(
                mainChatContext.latestSequence || 0,
                Number(message.sequence || 0));
            markActiveConversationRead();
            renderUnreadBadge(item, 0);
            return;
        }
        if (item) {
            const badge = item.querySelector(".admin-unread-badge");
            const existing = badge?.hidden ? 0 : Number(badge?.textContent || 0);
            renderUnreadBadge(item, existing + 1);
            const subtitle = item.querySelector(".admin-conversation-subtitle");
            if (subtitle) subtitle.textContent = message.instruction || "New message";
        } else {
            refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
        }
    }

    function handleTypingChanged(typing) {
        if (String(mainChatContext?.id) !== String(typing.conversationId)) return;
        const indicator = document.getElementById("admin-typing-indicator");
        if (!indicator) return;
        indicator.style.display = typing.isTyping ? "" : "none";
        const label = indicator.querySelector(".admin-typing-text");
        if (label) label.textContent = `${typing.displayName || "Someone"} is typing…`;
    }

    async function handleConversationChanged(payload) {
        const envelope = payload?.envelope || payload;
        const change = payload?.change || envelope?.data || envelope;
        const conversationId = Number(
            envelope?.conversationId || change?.conversationId);
        if (!Number.isSafeInteger(conversationId)) return;

        const currentUserId = Number(window.AdminCore?.getCurrentUser?.()?.id);
        const changeType = String(
            change?.changeType || envelope?.eventType || "").toLowerCase();
        const transferredAway = changeType.includes("transfer")
            && Number(change?.adminUserId) !== currentUserId;
        const archived = changeType.includes("archive");

        if (mainChatContext?.id === conversationId && (transferredAway || archived)) {
            await disableAndRefreshActive(
                archived ? "Conversation archived." : "Conversation transferred to another Admin.");
            return;
        }
        await refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
    }

    function handleConnectionState(state) {
        setConnectionStatus(state);
        if (state === "reconnected" || state === "connected") {
            refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
        }
    }

    function handleSendState(sendState) {
        if (Number(sendState?.conversationId) !== Number(mainChatContext?.id)) return;
        if (sendState.state === "pending") {
            setStatus("admin-chat-send-status", "Sending…", "is-pending");
        } else if (sendState.state === "sent") {
            setStatus("admin-chat-send-status", "Sent", "is-success");
        } else if (sendState.state === "failed") {
            setStatus(
                "admin-chat-send-status",
                "Send failed. Your draft was kept.",
                "is-error");
        }
    }

    function setConnectionStatus(state) {
        const normalized = state || getConnectionState();
        const display = {
            connected: ["Connected", "is-connected"],
            reconnected: ["Connection restored", "is-connected"],
            reconnecting: ["Reconnecting…", "is-warning"],
            disconnected: ["Offline", "is-error"],
            loading: ["Loading…", "is-warning"]
        }[normalized] || ["Connecting…", "is-warning"];
        setStatus("admin-chat-connection-status", display[0], display[1]);
        if (normalized === "reconnected") {
            window.setTimeout(() => {
                const status = document.getElementById("admin-chat-connection-status");
                if (status?.textContent === "Connection restored") {
                    setStatus("admin-chat-connection-status", "Connected", "is-connected");
                }
            }, 3000);
        }
    }

    function getConnectionState() {
        const connection = window.AdminSignalR?.getConnection?.();
        const state = String(connection?.state || "").toLowerCase();
        return state === "connected" ? "connected"
            : state === "reconnecting" ? "reconnecting"
                : state === "disconnected" ? "disconnected"
                    : "";
    }

    function showMobileDetail() {
        document.querySelector(".chat-dashboard-container")?.classList.add("show-detail");
        mainMessageInput?.focus();
    }

    function showMobileList() {
        void attachmentComposer?.resetForConversation();
        document.querySelector(".chat-dashboard-container")?.classList.remove("show-detail");
        document.querySelector(".admin-conversation-item.active")?.focus();
    }

    function supportsAttachments(context) {
        return Boolean(
            attachmentsEnabled
            && context?.isV2
            && ["group", "private", "ticket", "inquiry"]
                .includes(String(context.type || "").toLowerCase()));
    }

    function updateAttachmentAvailability() {
        const enabled = supportsAttachments(mainChatContext);
        if (attachmentButton) {
            attachmentButton.hidden = !enabled;
            attachmentButton.disabled = !enabled || isSending;
        }
        if (attachmentInput) attachmentInput.disabled = !enabled || isSending;
    }

    function toLegacyMessage(message) {
        const senderId = message.sender?.userId ?? message.sender?.id;
        return {
            id: message.id,
            instructionId: message.conversationId,
            instruction: message.text,
            dateTime: message.sentAt,
            senderName: message.sender?.displayName,
            insertUser: message.sender?.kind === "Admin" ? senderId : null,
            clientAuthUserId: message.sender?.kind === "Client" ? senderId : null,
            clientMessageId: message.clientMessageId,
            sequence: message.sequence,
            attachments: Array.isArray(message.attachments)
                ? message.attachments
                : (Array.isArray(message.safeAttachments) ? message.safeAttachments : [])
        };
    }

    async function refreshAdminConversations(currentClientId) {
        const icon = document.querySelector("#refresh-conversations-btn .fas");
        icon?.classList.add("fa-spin");
        try {
            await initializeChatsPage(currentClientId);
        } finally {
            icon?.classList.remove("fa-spin");
        }
    }

    async function openChatConversation(instructionId) {
        const id = Number(instructionId);
        let item = document.querySelector(`.admin-conversation-item[data-id="${id}"]`);
        if (!item) {
            await refreshAdminConversations(window.AdminCore?.getCurrentClientId?.());
            item = document.querySelector(`.admin-conversation-item[data-id="${id}"]`);
        }
        if (item) item.click();
        else AdminUtils.showNotification("Could not open chat conversation.", "error");
    }

    function openEnhancedFloatingChatBox(item, type) {
        const id = Number(item.id);
        const chatBoxId = `chatbox-${type}-${id}`;
        const existing = document.getElementById(chatBoxId);
        if (existing) {
            existing.classList.remove("collapsed");
            return;
        }

        const box = document.createElement("section");
        box.className = "floating-chat-box";
        box.id = chatBoxId;
        box.dataset.id = String(id);
        box.dataset.type = type;
        box.setAttribute("aria-label", `${type === "tkt" ? "Ticket" : "Inquiry"} chat`);

        const header = document.createElement("div");
        header.className = "chat-box-header";
        const title = document.createElement("span");
        title.className = "chat-box-title";
        title.textContent = `#${id} - ${item.subject || item.topic || "Conversation"} (${item.clientName || "Client"})`;
        const actions = document.createElement("div");
        actions.className = "chat-box-actions";
        actions.append(
            floatingAction("action-minimize", "Minimize", "fa-minus"),
            floatingAction("action-maximize", "Open in main chat", "fa-expand"),
            floatingAction("action-close", "Close", "fa-times"));
        header.append(title, actions);

        const body = document.createElement("div");
        body.className = "chat-box-body";
        const footer = document.createElement("div");
        footer.className = "chat-box-footer";
        const uploadList = document.createElement("div");
        uploadList.className = "attachment-upload-list floating-attachment-upload-list";
        uploadList.setAttribute("aria-live", "polite");
        const controls = document.createElement("div");
        controls.className = "floating-chat-controls";
        const fileInput = document.createElement("input");
        fileInput.type = "file";
        fileInput.className = "visually-hidden";
        fileInput.setAttribute("aria-label", "Choose attachments");
        const attach = document.createElement("button");
        attach.type = "button";
        attach.className = "btn btn-outline-secondary action-attach";
        attach.title = "Add attachments";
        attach.setAttribute("aria-label", "Add attachments");
        attach.hidden = !attachmentsEnabled;
        attach.disabled = !attachmentsEnabled;
        attach.innerHTML = '<i class="fas fa-paperclip" aria-hidden="true"></i>';
        const input = document.createElement("textarea");
        input.className = "form-control chat-message-input";
        input.rows = 1;
        input.maxLength = 4000;
        input.placeholder = "Type a message…";
        input.setAttribute("aria-label", "Message");
        const send = document.createElement("button");
        send.type = "button";
        send.className = "btn btn-primary action-send";
        send.title = "Send";
        send.disabled = true;
        send.innerHTML = '<i class="fas fa-paper-plane" aria-hidden="true"></i>';
        controls.append(fileInput, attach, input, send);
        footer.append(uploadList, controls);
        box.append(header, body, footer);
        floatingChatContainer?.appendChild(box);

        const composer = attachmentsEnabled
            ? window.CBSSupportAttachments?.createComposer({
                input: fileInput,
                button: attach,
                list: uploadList,
                getConversationId: () => id,
                onReadyChanged: () => updateFloatingSendState(box),
                onError: message => AdminUtils.showNotification(message, "error")
            })
            : null;
        if (composer) {
            floatingAttachmentComposers.set(box, composer);
            attach.disabled = false;
        }
        updateFloatingSendState(box);
        window.AdminSignalR?.joinConversation(id, true)
            .then(() => loadAndRenderFloatingChatMessages(id, body))
            .catch(error => renderFloatingError(body, error));
        wireFloatingChat(box);
    }

    function floatingAction(className, label, iconName) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = className;
        button.title = label;
        button.setAttribute("aria-label", label);
        button.innerHTML = `<i class="fas ${iconName}" aria-hidden="true"></i>`;
        return button;
    }

    function wireFloatingChat(box) {
        box.querySelector(".action-minimize")?.addEventListener("click", () =>
            box.classList.toggle("collapsed"));
        box.querySelector(".action-close")?.addEventListener("click", async () => {
            floatingAttachmentComposers.get(box)?.destroy();
            floatingAttachmentComposers.delete(box);
            await window.AdminSignalR?.leaveConversation(Number(box.dataset.id));
            box.remove();
        });
        box.querySelector(".action-maximize")?.addEventListener("click", () => {
            document.querySelector('[data-page="chats"]')?.click();
            openChatConversation(Number(box.dataset.id));
        });
        box.querySelector(".action-send")?.addEventListener("click", async () => {
            const input = box.querySelector(".chat-message-input");
            const text = input.value.trim();
            const composer = floatingAttachmentComposers.get(box);
            const attachmentIds = composer?.getReadyIds() || [];
            if (!text && attachmentIds.length === 0) return;
            try {
                await window.AdminSignalR.sendMessage(
                    Number(box.dataset.id),
                    text,
                    true,
                    attachmentIds);
                if (input.value.trim() === text) input.value = "";
                composer?.clearBound(attachmentIds);
                updateFloatingSendState(box);
            } catch (error) {
                AdminUtils.showNotification("Message was not sent.", "error");
            }
        });
        box.querySelector(".chat-message-input")?.addEventListener(
            "input",
            () => updateFloatingSendState(box));
    }

    function updateFloatingSendState(box) {
        const input = box.querySelector(".chat-message-input");
        const send = box.querySelector(".action-send");
        const composer = floatingAttachmentComposers.get(box);
        if (send) {
            send.disabled = !input?.value.trim()
                && !(composer?.getReadyIds().length > 0);
        }
    }

    async function loadAndRenderFloatingChatMessages(conversationId, container) {
        container.replaceChildren(createListMessage("Loading…", true));
        try {
            const messages = await messaging()?.reconcile(Number(conversationId));
            container.replaceChildren();
            messages.forEach(message =>
                appendFloatingMessage(container, toLegacyMessage(message)));
            AdminUtils.scrollToBottom(container);
        } catch (error) {
            renderFloatingError(container, error);
        }
    }

    function appendFloatingMessage(container, message) {
        if (message.id
            && container.querySelector(`[data-message-id="${Number(message.id)}"]`)) return;
        const currentUser = window.AdminCore?.getCurrentUser?.();
        const sent = String(message.insertUser) === String(currentUser?.id);
        const row = document.createElement("div");
        row.className = `message-row ${sent ? "sent" : "received"}`;
        if (message.id) row.dataset.messageId = String(message.id);
        const content = document.createElement("div");
        content.className = "message-content";
        const bubble = document.createElement("div");
        bubble.className = "message-bubble";
        const text = document.createElement("p");
        text.className = "message-text";
        text.textContent = message.instruction || "";
        bubble.appendChild(text);
        window.CBSSupportAttachments?.renderMessageAttachments(
            bubble,
            message.attachments || []);
        const stamp = document.createElement("span");
        stamp.className = "message-timestamp";
        stamp.textContent = `${message.senderName || (sent ? "Admin" : "Client")} · ${AdminUtils.formatTimestamp(message.dateTime)}`;
        content.append(bubble, stamp);
        row.appendChild(content);
        container.appendChild(row);
    }

    function renderFloatingError(container, error) {
        console.error("AdminChat: floating chat failed.", error);
        const message = document.createElement("p");
        message.className = "text-danger p-3";
        message.textContent = "Could not load messages.";
        container.replaceChildren(message);
    }

    return {
        initializeChatsPage,
        initializeChatSidebar,
        handleIncomingMessage,
        handleTypingChanged,
        handleConversationChanged,
        handleConnectionState,
        handleSendState,
        openChatConversation,
        openEnhancedFloatingChatBox,
        refreshAdminConversations
    };
})();
