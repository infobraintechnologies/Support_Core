"use strict";

document.addEventListener("DOMContentLoaded", () => {
    const bootstrapDataElement = document.getElementById("client-bootstrap-data");
    const serverData = bootstrapDataElement
        ? JSON.parse(bootstrapDataElement.textContent || "{}")
        : {};
    let currentUser = {
        name: String(serverData.currentUserName || ""),
        id: Number(serverData.currentUserId),
    };

    let currentChatContext = {};
    let chatSwitchRequest = 0;
    const conversationsById = new Map();
    const readSequences = new Map();
    let lastMessageDate = null;
    let currentTicketData = null;

    let ticketsDataTable = null;
    let inquiriesDataTable = null;

    let clientUnreadNotificationCount = 0;
    let clientNotificationPollingInterval = null;
    let isSendingMessage = false;
    let isCreatingTicket = false;
    let isCreatingInquiry = false;
    const modalOpeners = new WeakMap();

    const fullscreenBtn = document.getElementById("fullscreen-btn");
    const messageInput = document.getElementById("message-input");
    const sendButton = document.getElementById("send-button");
    const chatPanelBody = document.getElementById("chat-panel-body");
    const chatHeader = document.getElementById("chat-header");
    const chatHeading = document.getElementById("chat-heading");
    const chatSubheading = document.getElementById("chat-subheading");
    const chatFooter = document.getElementById("chat-footer");
    const conversationListContainer = document.getElementById("conversation-list-container");
    const conversationList = document.getElementById("client-conversation-list");
    const conversationListState = document.getElementById("conversation-list-state");
    const conversationCount = document.getElementById("client-conversation-count");
    const conversationSearch = document.getElementById("conversation-search");
    const conversationKindFilter = document.getElementById("conversation-kind-filter");
    const connectionStatus = document.getElementById("connection-status");
    const sendState = document.getElementById("send-state");
    const availableAdminsList = document.getElementById("available-admins-list");
    const availableAdminsState = document.getElementById("available-admins-state");
    const availableAdminsSearch = document.getElementById("available-admins-search");
    const newPrivateChatModalElement = document.getElementById("newPrivateChatModal");
    const mobileBackButton = document.getElementById("mobile-conversation-back");

    const supportTicketsTableE1 = $('#supportTicketsDataTable');
    const inquiriesTableE1 = $('#inquiriesDataTable');

    const messaging = window.CBSSupportMessaging.createClient({
        hubUrl: "/chathub",
        baseUrl: "/api/v1/conversations",
        draftScope: `client-user:${currentUser.id}`
    });
    const connection = messaging.connection;
    const attachmentsEnabled = document.body.dataset.attachmentsEnabled === "true";
    const attachmentButton = document.getElementById("attachment-button");
    const attachmentInput = document.getElementById("attachment-file-input");
    if (attachmentButton) attachmentButton.hidden = true;
    if (attachmentInput) attachmentInput.disabled = true;
    const attachmentComposer = attachmentsEnabled
        ? window.CBSSupportAttachments?.createComposer({
            input: attachmentInput,
            button: attachmentButton,
            list: document.getElementById("attachment-upload-list"),
            getConversationId: () => currentChatContext.id,
            onReadyChanged: () => updateSendButtonState(),
            onError: message => {
                if (sendState) sendState.textContent = message;
            }
        })
        : null;
    let typingTimer = null;

    const formatTimestamp = (d) => new Date(d).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

    const toLegacyMessage = (message) => ({
        id: message.id,
        instructionId: message.conversationId,
        instruction: message.text,
        dateTime: message.sentAt,
        senderName: message.sender?.displayName,
        insertUser: message.sender?.kind === "Admin" ? message.sender.userId : null,
        clientAuthUserId: message.sender?.kind === "Client" ? message.sender.userId : null,
        attachmentId: message.attachmentId,
        clientMessageId: message.clientMessageId,
        sequence: message.sequence,
        attachments: Array.isArray(message.attachments)
            ? message.attachments
            : (Array.isArray(message.safeAttachments) ? message.safeAttachments : [])
    });

    function updateSendButtonState() {
        if (!sendButton) return;
        const hasActiveConversation = Number(currentChatContext.id) > 0;
        const canAttach = supportsAttachments(currentChatContext);
        if (messageInput) messageInput.disabled = !hasActiveConversation || isSendingMessage;
        if (attachmentButton) {
            attachmentButton.hidden = !canAttach;
            attachmentButton.disabled = !canAttach;
        }
        if (attachmentInput) attachmentInput.disabled = !canAttach;
        sendButton.disabled = !hasActiveConversation
            || isSendingMessage
            || (!messageInput?.value.trim()
                && !(canAttach && attachmentComposer?.getReadyIds().length > 0));
    }

    function supportsAttachments(context) {
        const kind = String(context?.kind || context?.type || "").toLowerCase();
        const route = String(context?.route || "").toLowerCase();
        return Boolean(
            attachmentsEnabled
            && (["group", "private", "ticket", "inquiry"].includes(kind)
                || route.startsWith("ticket/")
                || route.startsWith("inquiry/")));
    }

    const scrollToBottom = () => {
        chatPanelBody.scrollTop = chatPanelBody.scrollHeight;
    };

    const isNearChatBottom = () => {
        if (!chatPanelBody) return true;
        return chatPanelBody.scrollHeight - chatPanelBody.scrollTop - chatPanelBody.clientHeight < 80;
    };

    function escapeHtml(text) {
        if (text === null || typeof text === 'undefined') return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    const generatePriorityBadge = (priority) => {
        const p = priority ? priority.toLowerCase() : 'normal';
        const badgeClass = `badge-priority-${p}`;
        const icon = p === 'urgent' ? 'bi bi-exclamation-triangle' :
            p === 'high' ? 'bi bi-exclamation-lg' :
                p === 'normal' || p === 'medium' ? 'bi bi-dash' : 'bi bi-arrow-down';

        return `<span class="badge ${badgeClass}">
        <i class="${icon} me-1" style="font-size: 0.7rem"></i>${escapeHtml(priority || 'Normal')}
    </span>`;
    };

    const formatDateForSeparator = (dStr) => {
        const d = new Date(dStr);
        const today = new Date();
        const yesterday = new Date(today);
        yesterday.setDate(yesterday.getDate() - 1);
        if (d.toDateString() === today.toDateString()) return "Today";
        if (d.toDateString() === yesterday.toDateString()) return "Yesterday";
        return d.toLocaleDateString([], { year: "numeric", month: "long", day: "numeric" });
    };

    function addDateSeparatorIfNeeded(msgDateStr) {
        if (!chatPanelBody) return;
        const dateStr = new Date(msgDateStr).toDateString();
        if (lastMessageDate !== dateStr) {
            lastMessageDate = dateStr;
            const ds = document.createElement("div");
            ds.className = "date-separator";
            const label = document.createElement("span");
            label.textContent = formatDateForSeparator(msgDateStr);
            ds.appendChild(label);
            chatPanelBody.appendChild(ds);
        }
    }
    function getTimeAgo(dateString) {
        const now = new Date();
        const date = new Date(dateString);
        const diffInMs = now - date;
        const diffInMinutes = Math.floor(diffInMs / (1000 * 60));
        const diffInHours = Math.floor(diffInMinutes / 60);
        const diffInDays = Math.floor(diffInHours / 24);

        if (diffInMinutes < 1) return 'Just now';
        if (diffInMinutes < 60) return `${diffInMinutes}m ago`;
        if (diffInHours < 24) return `${diffInHours}h ago`;
        if (diffInDays < 7) return `${diffInDays}d ago`;
        return date.toLocaleDateString();
    }

    function getNotificationIcon(type) {
        const icons = {
            'ticket': 'bi bi-ticket-perforated',
            'inquiry': 'bi bi-question-circle',
            'message': 'bi bi-chat',
            'status_change': 'bi bi-arrow-left-right'
        };
        return icons[type] || 'bi bi-bell';
    }

    function showNotificationToast(message, type = 'info') {
        let toastContainer = document.getElementById('toast-container');
        if (!toastContainer) {
            toastContainer = document.createElement('div');
            toastContainer.id = 'toast-container';
            toastContainer.className = 'position-fixed top-0 end-0 p-3';
            toastContainer.style.zIndex = '1055';
            document.body.appendChild(toastContainer);
        }

        const iconClass = type === 'success' ? 'bi-check-circle text-success' :
            type === 'error' ? 'bi-exclamation-triangle text-danger' :
                'bi-info-circle text-info';

        const toast = document.createElement('div');
        toast.className = 'toast';
        toast.setAttribute('role', 'alert');
        toast.setAttribute('aria-live', 'assertive');
        toast.setAttribute('aria-atomic', 'true');

        const header = document.createElement('div');
        header.className = 'toast-header';
        const icon = document.createElement('i');
        icon.className = `bi ${iconClass} me-2`;
        const strong = document.createElement('strong');
        strong.className = 'me-auto';
        strong.textContent = 'Notification';
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'btn-close';
        close.setAttribute('data-bs-dismiss', 'toast');
        close.setAttribute('aria-label', 'Close');
        header.append(icon, strong, close);

        const body = document.createElement('div');
        body.className = 'toast-body';
        body.textContent = message == null ? '' : String(message);

        toast.append(header, body);
        toastContainer.appendChild(toast);

        const bootstrapToast = new bootstrap.Toast(toast, {
            autohide: true,
            delay: 5000
        });

        bootstrapToast.show();

        toast.addEventListener('hidden.bs.toast', () => {
            toast.remove();
        });
    }

    function wireModalFocus(modalElement) {
        if (!modalElement || modalElement.dataset.focusRestoreWired === "true") return;

        modalElement.dataset.focusRestoreWired = "true";
        modalElement.addEventListener("show.bs.modal", event => {
            if (event.relatedTarget instanceof HTMLElement) {
                modalOpeners.set(modalElement, event.relatedTarget);
            }
        });
        modalElement.addEventListener("hidden.bs.modal", () => {
            const opener = modalOpeners.get(modalElement);
            if (opener?.isConnected && !opener.hasAttribute("disabled")) {
                opener.focus({ preventScroll: true });
            }
            modalOpeners.delete(modalElement);
        });
    }

    function setModalText(modalElement, id, value) {
        const element = modalElement?.querySelector(`#${id}`);
        if (element) element.textContent = value == null ? "" : String(value);
    }

    function setModalHtml(modalElement, id, value) {
        const element = modalElement?.querySelector(`#${id}`);
        if (element) element.innerHTML = value;
    }

    function populateTicketDetailsModal(ticketData, modalElement) {
        console.log('Populating modal with:', ticketData);

        setModalText(modalElement, "details-id", `#${ticketData.id || "N/A"}`);
        setModalText(modalElement, "details-subject", ticketData.subject || "N/A");

        if (ticketData.date) {
            const date = new Date(ticketData.date);
            setModalText(modalElement, "details-date", date.toLocaleString());
        } else {
            setModalText(modalElement, "details-date", "N/A");
        }

        setModalText(modalElement, "details-createdBy", ticketData.createdBy || "N/A");
        setModalText(modalElement, "details-resolvedBy", ticketData.resolvedBy || "N/A");

        const status = ticketData.status || 'Pending';
        const statusClass = `badge-status-${status.toLowerCase()}`;
        setModalHtml(modalElement, "details-status", `<span class="badge ${statusClass}">${escapeHtml(status)}</span>`);

        setModalHtml(modalElement, "details-priority", generatePriorityBadge(ticketData.priority));
        setModalText(modalElement, "details-description", ticketData.instruction || ticketData.description || "No description provided.");

        let remarksText = 'N/A';
        try {
            if (ticketData.remarks) {
                const remarksObj = JSON.parse(ticketData.remarks);
                remarksText = remarksObj.userremarks || remarksObj.remarks || ticketData.remarks;
            }
        } catch (e) {
            remarksText = ticketData.remarks || 'N/A';
        }
        setModalText(modalElement, "details-remarks", remarksText);

        if (ticketData.expiryDate) {
            const expiryDate = new Date(ticketData.expiryDate);
            setModalText(modalElement, "details-expiryDate", expiryDate.toLocaleString());
        } else {
            setModalText(modalElement, "details-expiryDate", "N/A");
        }
    }

    function populateInquiryDetailsModal(inquiryData, modalElement) {
        console.log('Populating inquiry modal with:', inquiryData);

        setModalText(modalElement, "inquiry-details-id", `#INQ-${inquiryData.id || "N/A"}`);
        setModalText(modalElement, "inquiry-details-topic", inquiryData.topic || "N/A");

        if (inquiryData.date) {
            const date = new Date(inquiryData.date);
            setModalText(modalElement, "inquiry-details-date", date.toLocaleString());
        } else {
            setModalText(modalElement, "inquiry-details-date", "N/A");
        }

        setModalText(modalElement, "inquiry-details-inquiredBy", inquiryData.inquiredBy || "N/A");

        const outcome = inquiryData.outcome || 'Pending';
        const mappedOutcome = outcome === 'Completed' ? 'Resolved' : outcome;
        const outcomeClass = `badge-status-${mappedOutcome.toLowerCase()}`;
        setModalHtml(modalElement, "inquiry-details-outcome", `<span class="badge ${outcomeClass}">${escapeHtml(outcome)}</span>`);

        setModalText(modalElement, "inquiry-details-description", inquiryData.description || inquiryData.instruction || "No description provided.");
    }

    if (fullscreenBtn) {
        const fullscreenIcon = fullscreenBtn.querySelector("i");
        fullscreenBtn.addEventListener("click", () => {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen().catch(err => console.error(err.message));
            } else if (document.exitFullscreen) {
                document.exitFullscreen();
            }
        });
        document.addEventListener("fullscreenchange", () => {
            if (document.fullscreenElement) {
                fullscreenIcon.classList.remove("bi-arrows-fullscreen");
                fullscreenIcon.classList.add("bi-fullscreen-exit");
            } else {
                fullscreenIcon.classList.remove("bi-fullscreen-exit");
                fullscreenIcon.classList.add("bi-arrows-fullscreen");
            }
        });
    }

    function appendTextElement(parent, tagName, className, text) {
        const element = document.createElement(tagName);
        if (className) element.className = className;
        element.textContent = String(text ?? "");
        parent.appendChild(element);
        return element;
    }

    function setChatPanelState(message, type = "empty", retryAction = null) {
        if (!chatPanelBody) return;
        chatPanelBody.replaceChildren();
        const state = document.createElement("div");
        state.className = `client-chat-state client-chat-${type}`;
        state.setAttribute("role", type === "error" ? "alert" : "status");
        appendTextElement(state, "span", "", message);
        if (retryAction) {
            const retry = document.createElement("button");
            retry.type = "button";
            retry.className = "btn btn-sm btn-outline-primary";
            retry.textContent = "Retry";
            retry.addEventListener("click", retryAction, { once: true });
            state.appendChild(retry);
        }
        chatPanelBody.appendChild(state);
    }

    function displayMessage(msg, isHistory = false) {
        if (!chatPanelBody || !msg?.dateTime) return;
        const shouldAutoScroll = !isHistory && isNearChatBottom();
        const messageId = Number(msg.id);
        if (Number.isSafeInteger(messageId)
            && chatPanelBody.querySelector(`[data-message-id="${messageId}"]`)) {
            return;
        }

        addDateSeparatorIfNeeded(msg.dateTime);
        const isSent = msg.clientAuthUserId != null && Number(msg.clientAuthUserId) === currentUser.id;
        const row = document.createElement("div");
        row.className = `message-row ${isSent ? "sent" : "received"}`;
        if (Number.isSafeInteger(messageId)) row.dataset.messageId = String(messageId);
        if (msg.clientMessageId) row.dataset.clientMessageId = String(msg.clientMessageId);

        const bubble = document.createElement("div");
        bubble.className = "message-bubble";
        appendTextElement(bubble, "div", "message-sender", msg.senderName || "Support");
        appendTextElement(bubble, "p", "message-text", msg.instruction || "");
        window.CBSSupportAttachments?.renderMessageAttachments(
            bubble,
            msg.attachments || []);
        appendTextElement(bubble, "div", "message-timestamp", formatTimestamp(msg.dateTime));
        row.appendChild(bubble);
        chatPanelBody.appendChild(row);

        if (shouldAutoScroll) scrollToBottom();
    }

    function getConversationName(conversation) {
        if (conversation.kind === "Private") {
            return String(conversation.adminDisplayName || "Support administrator");
        }
        if (conversation.kind === "Ticket") return `Ticket #${conversation.id}`;
        if (conversation.kind === "Inquiry") return `Inquiry #${conversation.id}`;
        return "Group support";
    }

    function getConversationSubtitle(conversation) {
        if (conversation.kind === "Private") return "Private conversation";
        if (conversation.kind === "Ticket") return "Support ticket conversation";
        if (conversation.kind === "Inquiry") return "Inquiry conversation";
        return "Everyone in your organization";
    }

    function getConversationIconClass(kind) {
        if (kind === "Private") return "bi-person";
        if (kind === "Ticket") return "bi-ticket-perforated";
        if (kind === "Inquiry") return "bi-question-circle";
        return "bi-people";
    }

    function createConversationIcon(kind) {
        const surface = document.createElement("span");
        surface.className = "client-conversation-icon";
        const icon = document.createElement("i");
        icon.className = `bi ${getConversationIconClass(kind)}`;
        icon.setAttribute("aria-hidden", "true");
        surface.appendChild(icon);
        return surface;
    }

    function createConversationItem(conversation) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "list-group-item list-group-item-action conversation-item";
        item.dataset.id = String(conversation.id);
        item.dataset.kind = String(conversation.kind || "");
        item.dataset.searchText = `${getConversationName(conversation)} ${getConversationSubtitle(conversation)}`
            .toLocaleLowerCase();
        item.setAttribute("aria-pressed", "false");

        const content = document.createElement("div");
        content.className = "client-conversation-item-content";
        content.appendChild(createConversationIcon(conversation.kind));

        const copy = document.createElement("span");
        copy.className = "client-conversation-copy";
        appendTextElement(copy, "span", "client-conversation-name", getConversationName(conversation));
        appendTextElement(copy, "small", "text-muted conversation-subtitle", getConversationSubtitle(conversation));
        content.appendChild(copy);

        const unreadCount = Math.max(0, Number(conversation.unreadCount) || 0);
        const badge = appendTextElement(content, "span", "conversation-unread-badge", unreadCount > 99 ? "99+" : unreadCount);
        badge.hidden = unreadCount === 0;
        badge.setAttribute("aria-label", `${unreadCount} unread message${unreadCount === 1 ? "" : "s"}`);
        item.appendChild(content);
        return item;
    }

    function renderConversationList(conversations) {
        if (!conversationList || !conversationListState) return;
        conversationList.replaceChildren();
        conversationsById.clear();

        let conversationTotal = 0;
        for (const conversation of conversations) {
            const id = Number(conversation.id);
            if (!Number.isSafeInteger(id) || id <= 0) continue;
            conversationsById.set(id, { ...conversation, id });
            conversationList.appendChild(createConversationItem(conversation));
            conversationTotal += 1;
        }

        if (conversationCount) {
            conversationCount.textContent = `${conversationTotal} conversation${conversationTotal === 1 ? "" : "s"}`;
        }

        const hasGroup = conversations.some(conversation => conversation.kind === "Group");
        if (!hasGroup) {
            const startGroup = document.createElement("button");
            startGroup.type = "button";
            startGroup.className = "list-group-item list-group-item-action conversation-item client-start-group";
            startGroup.dataset.action = "start-group";
            startGroup.dataset.kind = "Group";
            startGroup.dataset.searchText = "group support organization";
            const content = document.createElement("div");
            content.className = "client-conversation-item-content";
            content.appendChild(createConversationIcon("Group"));
            const label = document.createElement("span");
            label.className = "client-conversation-copy";
            appendTextElement(label, "span", "client-conversation-name", "Group support");
            appendTextElement(label, "small", "text-muted conversation-subtitle", "Start your organization conversation");
            content.appendChild(label);
            startGroup.appendChild(content);
            conversationList.prepend(startGroup);
        }

        conversationListState.replaceChildren();
        conversationListState.hidden = conversationList.childElementCount > 0;
        conversationListContainer?.setAttribute("aria-busy", "false");
        if (!conversationList.childElementCount) {
            conversationListState.hidden = false;
            conversationListState.textContent = "No conversations yet. Create a ticket or inquiry when you need support.";
        }

        if (currentChatContext.id) {
            const activeItem = conversationList.querySelector(`[data-id="${Number(currentChatContext.id)}"]`);
            activeItem?.classList.add("active");
            activeItem?.setAttribute("aria-pressed", "true");
        }
        applyConversationFilters();
    }

    function applyConversationFilters() {
        const query = String(conversationSearch?.value || "").trim().toLocaleLowerCase();
        const kind = String(conversationKindFilter?.value || "all").toLocaleLowerCase();
        let visible = 0;
        for (const item of conversationList?.querySelectorAll(".conversation-item") || []) {
            const itemKind = String(item.dataset.kind || "").toLocaleLowerCase();
            const matchesKind = kind === "all" || itemKind === kind;
            const matchesText = !query
                || String(item.dataset.searchText || "").includes(query);
            item.hidden = !(matchesKind && matchesText);
            if (!item.hidden) visible += 1;
        }
        if (conversationListState && conversationList?.childElementCount) {
            conversationListState.hidden = visible > 0;
            conversationListState.textContent = visible > 0
                ? ""
                : "No conversations match your filters.";
        }
    }

    async function loadConversations() {
        if (!conversationListState) return;
        conversationListContainer?.setAttribute("aria-busy", "true");
        conversationListState.hidden = false;
        conversationListState.textContent = "Loading conversations…";
        try {
            const result = await messaging.listConversations({ limit: 100 });
            const conversations = Array.isArray(result)
                ? result
                : (Array.isArray(result?.items) ? result.items : []);
            renderConversationList(conversations);
        } catch (error) {
            console.error("Unable to load conversations:", error);
            conversationListContainer?.setAttribute("aria-busy", "false");
            conversationListState.replaceChildren();
            appendTextElement(conversationListState, "span", "", "Conversations could not be loaded.");
            const retry = appendTextElement(conversationListState, "button", "btn btn-sm btn-outline-primary", "Retry");
            retry.type = "button";
            retry.addEventListener("click", loadConversations, { once: true });
        }
    }

    function updateActiveConversationItem() {
        document.querySelectorAll(".conversation-item.active").forEach(element => {
            element.classList.remove("active");
            element.setAttribute("aria-pressed", "false");
        });
        const activeItem = conversationList?.querySelector(`[data-id="${Number(currentChatContext.id)}"]`);
        activeItem?.classList.add("active");
        activeItem?.setAttribute("aria-pressed", "true");
    }

    async function switchChatContext(contextData) {
        const id = Number(contextData.id);
        if (id > 0 && id === Number(currentChatContext.id)) {
            document.querySelector(".dashboard-container")?.classList.add("client-chat-detail-open");
            return;
        }
        const requestId = ++chatSwitchRequest;
        if (currentChatContext.id && messageInput) {
            messaging.store.saveDraft(currentChatContext.id, messageInput.value);
            messaging.cancelReconcile?.(currentChatContext.id);
            messaging.leave(currentChatContext.id).catch(() => {});
            attachmentComposer?.resetForConversation();
        }

        const listedConversation = conversationsById.get(id);
        currentChatContext = {
            ...(listedConversation || {}),
            id,
            name: listedConversation ? getConversationName(listedConversation) : contextData.name,
            type: listedConversation?.kind || contextData.type,
            route: contextData.route
        };
        updateActiveConversationItem();
        if (chatHeading) chatHeading.textContent = currentChatContext.name || "Conversation";
        if (chatSubheading) {
            chatSubheading.textContent = listedConversation
                ? getConversationSubtitle(listedConversation)
                : "Support conversation";
        }
        if (messageInput) {
            messageInput.value = messaging.store.loadDraft(currentChatContext.id);
        }
        document.querySelector(".dashboard-container")?.classList.add("client-chat-detail-open");

        try {
            setChatPanelState("Loading messages…", "loading");
            await messaging.join(currentChatContext.id);
            if (requestId !== chatSwitchRequest) return;
            await loadMessagesForConversation(currentChatContext.id);
            if (requestId !== chatSwitchRequest) return;
            updateSendButtonState();
        } catch (error) {
            if (requestId !== chatSwitchRequest || error?.name === "AbortError") return;
            console.error("Failed to open conversation:", error);
            setChatPanelState(
                "This conversation could not be opened.",
                "error",
                () => switchChatContext(currentChatContext));
            updateSendButtonState();
        }
    }

    async function loadMessagesForConversation(conversationId) {
        if (!chatPanelBody) return;
        setChatPanelState("Loading messages…", "loading");
        lastMessageDate = null;
        try {
            const messages = await messaging.reconcile(conversationId);
            if (Number(currentChatContext.id) !== Number(conversationId)) return;
            chatPanelBody.replaceChildren();
            lastMessageDate = null;
            messages.forEach(msg => displayMessage(
                toLegacyMessage(msg),
                true));
            const pendingMessages = messaging.store.listPending
                ? messaging.store.listPending(conversationId)
                : [];
            pendingMessages.forEach(pending => renderPendingMessage(
                conversationId,
                pending.clientMessageId,
                pending.state));
            if (!messages.length && !pendingMessages.length) {
                setChatPanelState("No messages yet. Say hello to start the conversation.", "empty");
            }
            scrollToBottom();
            await advanceActiveReadCursor();
        } catch (error) {
            console.error("Error loading messages:", error);
            setChatPanelState(
                "Messages could not be loaded.",
                "error",
                () => loadMessagesForConversation(conversationId));
        }
    }

    function setUnreadBadge(conversationId, unreadCount) {
        const item = conversationList?.querySelector(`[data-id="${Number(conversationId)}"]`);
        const badge = item?.querySelector(".conversation-unread-badge");
        if (!badge) return;
        const count = Math.max(0, Number(unreadCount) || 0);
        badge.textContent = count > 99 ? "99+" : String(count);
        badge.hidden = count === 0;
        badge.setAttribute("aria-label", `${count} unread message${count === 1 ? "" : "s"}`);
        item.classList.toggle("has-unread", count > 0);
    }

    async function advanceActiveReadCursor() {
        const conversationId = Number(currentChatContext.id);
        const mobileListIsVisible = globalThis.matchMedia?.("(max-width: 768px)").matches
            && !document.querySelector(".dashboard-container")?.classList.contains("client-chat-detail-open");
        if (!Number.isSafeInteger(conversationId)
            || conversationId <= 0
            || document.visibilityState === "hidden"
            || mobileListIsVisible) {
            return;
        }

        const throughSequence = messaging.store.getLastSequence(conversationId);
        if (throughSequence <= (readSequences.get(conversationId) || 0)) return;
        try {
            await messaging.advanceRead(conversationId, throughSequence);
            readSequences.set(conversationId, throughSequence);
            const conversation = conversationsById.get(conversationId);
            if (conversation) {
                conversation.lastReadSequence = throughSequence;
                conversation.unreadCount = 0;
            }
            setUnreadBadge(conversationId, 0);
        } catch (error) {
            console.error("Unable to advance conversation read cursor:", error);
        }
    }

    function filterAvailableAdmins() {
        const query = String(availableAdminsSearch?.value || "").trim().toLocaleLowerCase();
        let visible = 0;
        for (const button of availableAdminsList?.querySelectorAll(".client-admin-option") || []) {
            const matches = !query
                || String(button.dataset.searchName || "").includes(query);
            button.hidden = !matches;
            if (matches) visible += 1;
        }
        if (availableAdminsState && availableAdminsList?.childElementCount) {
            availableAdminsState.hidden = visible > 0;
            availableAdminsState.textContent = visible > 0
                ? ""
                : "No administrators match your search.";
        }
    }

    async function loadAvailableAdmins() {
        if (!availableAdminsList || !availableAdminsState) return;
        availableAdminsList.replaceChildren();
        if (availableAdminsSearch) availableAdminsSearch.value = "";
        availableAdminsState.hidden = false;
        availableAdminsState.textContent = "Loading administrators…";
        try {
            const admins = await messaging.getAvailableAdmins();
            availableAdminsState.hidden = admins.length > 0;
            availableAdminsState.textContent = admins.length
                ? ""
                : "No administrators are currently available.";
            for (const admin of admins) {
                const adminId = Number(admin.id);
                if (!Number.isSafeInteger(adminId) || adminId <= 0) continue;
                const button = document.createElement("button");
                button.type = "button";
                button.className = "list-group-item list-group-item-action client-admin-option";
                button.dataset.adminId = String(adminId);
                button.dataset.searchName = String(admin.displayName || "").toLocaleLowerCase();
                const avatar = appendTextElement(
                    button,
                    "span",
                    "avatar-initials avatar-bg-blue",
                    String(admin.displayName || "Support").trim().charAt(0).toUpperCase() || "S");
                avatar.setAttribute("aria-hidden", "true");
                appendTextElement(button, "span", "client-admin-name", admin.displayName || "Support administrator");
                availableAdminsList.appendChild(button);
            }
        } catch (error) {
            availableAdminsState.hidden = false;
            availableAdminsState.replaceChildren();
            if (error?.status === 404) {
                availableAdminsState.textContent = "Private messaging is not available.";
                document.getElementById("new-private-chat-btn")?.classList.add("d-none");
                return;
            }
            console.error("Unable to load available administrators:", error);
            appendTextElement(availableAdminsState, "span", "", "Administrators could not be loaded.");
            const retry = appendTextElement(availableAdminsState, "button", "btn btn-sm btn-outline-primary", "Retry");
            retry.type = "button";
            retry.addEventListener("click", loadAvailableAdmins, { once: true });
        }
    }

    async function createPrivateConversation(adminButton) {
        const adminId = Number(adminButton.dataset.adminId);
        if (!Number.isSafeInteger(adminId) || adminId <= 0) return;
        const buttons = availableAdminsList?.querySelectorAll("button") || [];
        buttons.forEach(button => { button.disabled = true; });
        if (availableAdminsState) {
            availableAdminsState.hidden = false;
            availableAdminsState.textContent = "Opening private conversation…";
        }
        try {
            const conversation = await messaging.getOrCreatePrivate(adminId);
            await loadConversations();
            bootstrap.Modal.getOrCreateInstance(newPrivateChatModalElement).hide();
            await switchChatContext(conversation);
        } catch (error) {
            console.error("Unable to create private conversation:", error);
            if (availableAdminsState) {
                availableAdminsState.textContent = "The private conversation could not be opened. Please retry.";
            }
        } finally {
            buttons.forEach(button => { button.disabled = false; });
        }
    }

    function findPendingMessage(clientMessageId) {
        return Array.from(chatPanelBody?.querySelectorAll(".message-row.client-message-pending") || [])
            .find(row => row.dataset.clientMessageId === String(clientMessageId));
    }

    function renderPendingMessage(conversationId, clientMessageId, state) {
        if (!chatPanelBody || Number(currentChatContext.id) !== Number(conversationId)) return;
        const pending = messaging.store.findPending
            ? messaging.store.findPending(
                conversationId,
                item => String(item.clientMessageId).toLowerCase() === String(clientMessageId).toLowerCase())
            : messaging.store.loadPending(conversationId);
        if (!pending) return;
        let row = findPendingMessage(clientMessageId);
        if (!row) {
            if (chatPanelBody.querySelector(".client-chat-state")) chatPanelBody.replaceChildren();
            row = document.createElement("div");
            row.className = "message-row sent client-message-pending";
            row.dataset.clientMessageId = String(clientMessageId);
            const bubble = document.createElement("div");
            bubble.className = "message-bubble";
            appendTextElement(bubble, "p", "message-text", pending.text);
            row.appendChild(bubble);
            chatPanelBody.appendChild(row);
        }
        row.classList.toggle("failed", state === "failed");
        row.querySelector(".client-send-detail")?.remove();
        const detail = document.createElement("div");
        detail.className = "client-send-detail";
        detail.textContent = state === "failed" ? "Not sent" : "Sending…";
        if (state === "failed") {
            const retry = document.createElement("button");
            retry.type = "button";
            retry.className = "btn btn-link btn-sm retry-message";
            retry.textContent = "Retry";
            retry.dataset.clientMessageId = String(clientMessageId);
            detail.appendChild(retry);
        }
        row.querySelector(".message-bubble")?.appendChild(detail);
        scrollToBottom();
    }

    async function sendMessage(clientMessageId = null) {
        if (!messageInput) return;
        if (isSendingMessage && !clientMessageId) return;

        const conversationId = Number(currentChatContext.id);
        if (!Number.isSafeInteger(conversationId) || conversationId <= 0) {
            if (sendState) sendState.textContent = "Select a conversation before sending.";
            updateSendButtonState();
            return;
        }

        const pending = clientMessageId && messaging.store.findPending
            ? messaging.store.findPending(
                conversationId,
                item => String(item.clientMessageId).toLowerCase() === String(clientMessageId).toLowerCase())
            : (clientMessageId ? messaging.store.loadPending(conversationId) : null);
        const rawMessageText = pending?.text || messageInput.value;
        const messageText = rawMessageText.trim();
        const attachmentIds = supportsAttachments(currentChatContext)
            ? pending?.attachmentIds || attachmentComposer?.getReadyIds() || []
            : [];
        if (!messageText && attachmentIds.length === 0) {
            return;
        }

        if (!clientMessageId) {
            messageInput.value = "";
            updateSendButtonState();
        }
        isSendingMessage = true;
        updateSendButtonState();
        try {
            if (clientMessageId && messaging.retry) {
                await messaging.retry(conversationId, clientMessageId);
            } else {
                await messaging.send(
                    conversationId,
                    messageText,
                    clientMessageId,
                    attachmentIds);
            }
            attachmentComposer?.clearBound(attachmentIds);
            updateSendButtonState();
        } catch (error) {
            console.error("Error sending message:", error);
            if (!messageInput.value && Number(currentChatContext.id) === conversationId) {
                messageInput.value = messaging.store.loadDraft(conversationId) || messageText;
            }
            if (sendState) sendState.textContent = "Message failed to send. Use Retry to try again.";
            updateSendButtonState();
        } finally {
            isSendingMessage = false;
            updateSendButtonState();
        }
    }

    async function loadClientNotifications() {
        try {
            const response = await fetch('/api/v1/notifications?limit=20', { credentials: 'same-origin' });
            if (!response.ok) throw new Error('Failed to load notifications');

            const page = await response.json();
            const allNotifications = processClientNotifications(page.items || []);

            updateClientNotificationBadge(page.unreadCount || 0);
            renderClientNotifications(allNotifications);

            return allNotifications;
        } catch (error) {
            console.error('Error loading client notifications:', error);
            renderClientNotificationLoadError();
            return [];
        }
    }

    function processClientNotifications(notificationRows) {
        const notifications = [];

        notificationRows.forEach(row => {
            let notification = {
                id: row.id,
                title: row.title || 'Support update',
                message: row.message || 'A support case was updated.',
                type: 'message',
                entityId: row.caseId,
                entityType: 'message',
                createdAt: row.createdAt,
                isRead: Boolean(row.readAt)
            };

            if (row.eventType.startsWith('Ticket')) {
                notification.type = 'ticket';
                notification.entityType = 'ticket';
            } else if (row.eventType.startsWith('Inquiry')) {
                notification.type = 'inquiry';
                notification.entityType = 'inquiry';
            }

            notification.timeAgo = getTimeAgo(notification.createdAt);
            notification.icon = getNotificationIcon(notification.type);
            notifications.push(notification);
        });

        notifications.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
        return notifications;
    }

    function updateClientNotificationBadge(count) {
        const badge = document.getElementById('client-notification-count');

        if (badge) {
            if (count > 0) {
                badge.textContent = count > 99 ? '99+' : count.toString();
                badge.style.display = 'block';
            } else {
                badge.style.display = 'none';
            }
        }

        clientUnreadNotificationCount = count;
    }

    function renderClientNotifications(notifications) {
        const container = document.getElementById('client-notification-list');

        if (!container) return;

        container.replaceChildren();

        if (!notifications || notifications.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'notification-empty';
            const icon = document.createElement('i');
            icon.className = 'bi bi-bell-slash fs-4 mb-2';
            const text = document.createElement('p');
            text.textContent = 'No notifications yet';
            empty.append(icon, text);
            container.appendChild(empty);
            return;
        }

        notifications.forEach(notification => {
            const item = document.createElement('button');
            item.type = 'button';
            item.className = `notification-item${notification.isRead ? '' : ' unread'}`;
            item.dataset.id = String(notification.id);
            item.dataset.entityId = String(notification.entityId ?? '');
            item.dataset.entityType = String(notification.entityType ?? '');

            if (!notification.isRead) {
                const unread = document.createElement('span');
                unread.className = 'visually-hidden notification-unread-label';
                unread.textContent = 'Unread notification. ';
                item.appendChild(unread);
            }

            const content = document.createElement('div');
            content.className = 'notification-content';

            const iconWrapper = document.createElement('div');
            iconWrapper.className = `notification-icon ${notification.type}`;
            const icon = document.createElement('i');
            icon.className = notification.icon;
            icon.setAttribute('aria-hidden', 'true');
            iconWrapper.appendChild(icon);

            const text = document.createElement('div');
            text.className = 'notification-text';

            const title = document.createElement('div');
            title.className = 'notification-title';
            title.textContent = notification.title;

            const message = document.createElement('div');
            message.className = 'notification-message';
            message.textContent = notification.message;

            const time = document.createElement('time');
            time.className = 'notification-time';
            time.textContent = notification.timeAgo;
            if (notification.createdAt) time.dateTime = notification.createdAt;

            text.append(title, message, time);
            content.append(iconWrapper, text);
            item.appendChild(content);
            container.appendChild(item);
        });
    }

    function renderClientNotificationLoadError() {
        const container = document.getElementById('client-notification-list');
        if (!container) return;
        const state = document.createElement('div');
        state.className = 'notification-empty';
        state.setAttribute('role', 'alert');
        const message = document.createElement('p');
        message.textContent = "Couldn't load notifications. Check your connection and try again.";
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.className = 'btn btn-sm btn-outline-primary';
        retry.textContent = 'Retry';
        retry.addEventListener('click', loadClientNotifications, { once: true });
        state.append(message, retry);
        container.replaceChildren(state);
    }

    async function markClientNotificationAsRead(notificationId) {
        try {
            const response = await fetch(`/api/v1/notifications/${notificationId}/read`, {
                method: 'PUT',
                headers: notificationRequestHeaders(),
                credentials: 'same-origin'
            });

            if (response.ok) {
                const notificationElement = document.querySelector(`[data-id="${notificationId}"]`);
                if (notificationElement && notificationElement.classList.contains('unread')) {
                    notificationElement.classList.remove('unread');
                    notificationElement.querySelector('.notification-unread-label')?.remove();
                }
                const changed = await response.json();
                updateClientNotificationBadge(changed.unreadCount || 0);
            }
        } catch (error) {
            console.error('Error marking notification as read:', error);
        }
    }

    async function markAllClientNotificationsAsRead() {
        try {
            const response = await fetch('/api/v1/notifications/read-all', {
                method: 'PUT',
                headers: notificationRequestHeaders(),
                credentials: 'same-origin'
            });

            if (response.ok) {
                document.querySelectorAll('.notification-item.unread').forEach(item => {
                    item.classList.remove('unread');
                    item.querySelector('.notification-unread-label')?.remove();
                });

                const result = await response.json();
                updateClientNotificationBadge(result.unreadCount || 0);
                showNotificationToast('All notifications marked as read.', 'success');
            }
        } catch (error) {
            console.error('Error marking all notifications as read:', error);
            showNotificationToast('Notifications could not be marked as read.', 'error');
        }
    }

    function notificationRequestHeaders() {
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        return token ? { 'RequestVerificationToken': token } : {};
    }

    function initializeClientNotifications() {
        console.log('🔔 Initializing client notifications...');

        const notificationBtn = document.getElementById('client-notification-btn');
        const notificationMenu = document.getElementById('client-notification-menu');

        if (notificationBtn && notificationMenu) {
            notificationBtn.addEventListener('click', async (e) => {
                e.stopPropagation();

                const isVisible = notificationMenu.classList.contains('show');

                if (isVisible) {
                    notificationMenu.classList.remove('show');
                    notificationBtn.setAttribute('aria-expanded', 'false');
                    return;
                }

                try {
                    await loadClientNotifications();
                    notificationMenu.classList.add('show');
                    notificationBtn.setAttribute('aria-expanded', 'true');
                    notificationMenu.querySelector('.notification-item, #client-mark-all-read-btn')?.focus();
                } catch (error) {
                    console.error('❌ Error loading notifications:', error);
                }
            });

            document.addEventListener('click', (e) => {
                if (!e.target.closest('.client-notification-container')) {
                    notificationMenu.classList.remove('show');
                    notificationBtn.setAttribute('aria-expanded', 'false');
                }
            });

            notificationMenu.addEventListener('keydown', e => {
                if (e.key === 'Escape') {
                    notificationMenu.classList.remove('show');
                    notificationBtn.setAttribute('aria-expanded', 'false');
                    notificationBtn.focus();
                }
            });

            notificationMenu.addEventListener('click', async (e) => {
                const notificationItem = e.target.closest('.notification-item');
                if (notificationItem) {
                    const notificationId = notificationItem.dataset.id;
                    const entityId = notificationItem.dataset.entityId;
                    const entityType = notificationItem.dataset.entityType;

                    if (notificationItem.classList.contains('unread')) {
                        await markClientNotificationAsRead(notificationId);
                    }

                    if (entityType === 'message') {
                        await switchChatContext({
                            id: entityId,
                            name: 'Support Chat',
                            type: 'support',
                            route: 'support-group'
                        });
                    } else if (entityType === 'ticket' && entityId) {
                        if (ticketsDataTable) {
                            ticketsDataTable.ajax.reload();
                        }
                    } else if (entityType === 'inquiry' && entityId) {
                        if (inquiriesDataTable) {
                            inquiriesDataTable.ajax.reload();
                        }
                    }

                    notificationMenu.classList.remove('show');
                }
            });

            const markAllReadBtn = document.getElementById('client-mark-all-read-btn');
            if (markAllReadBtn) {
                markAllReadBtn.addEventListener('click', markAllClientNotificationsAsRead);
            }

            console.log('✅ Client notifications initialized');
        }

        loadClientNotifications();

        if (clientNotificationPollingInterval) {
            clearInterval(clientNotificationPollingInterval);
        }
        clientNotificationPollingInterval = setInterval(loadClientNotifications, 30000);
    }

    function initializeTicketSystem() {
        const createTicketModalEl = document.getElementById("newSupportTicketModal");
        const createInquiryModalEl = document.getElementById("newInquiryModal");

        const createTicketModal = createTicketModalEl ? bootstrap.Modal.getOrCreateInstance(createTicketModalEl) : null;
        const createInquiryModal = createInquiryModalEl ? bootstrap.Modal.getOrCreateInstance(createInquiryModalEl) : null;

        const createTicketForm = document.getElementById("supportTicketForm");
        const createInquiryForm = document.getElementById("inquiryForm");

        if (createTicketForm) {
            createTicketForm.addEventListener("submit", async (e) => {
                e.preventDefault();
                if (isCreatingTicket) return;

                const subjectSelect = document.getElementById("ticketSubject");
                const descriptionInput = document.getElementById("ticketDescription")
                const remarksInput = document.getElementById("ticketRemarks")
                const expiryDateInput = document.getElementById("ticketExpiryDate")

                if (!subjectSelect) {
                    console.error("Could not find element with ID 'ticketSubject'.");
                    alert("An error occured. Could not find the subject field");
                    return;
                }

                const ticketTypeRoute = subjectSelect.value;
                const description = descriptionInput.value;
                const remarks = remarksInput.value;
                const expiryDate = expiryDateInput.value;
                const priority = document.getElementById("ticketPriority").value;

                if (!ticketTypeRoute || !description) {
                    alert("Please fill all the required fields for the ticket.");
                    return;
                }

                const chatMessage = {
                    Instruction: description,
                    Remarks: remarks,
                    Priority: priority,
                    expiryDate: expiryDate,
                    InstructionId: null,
                };

                isCreatingTicket = true;
                const submitButton = createTicketForm.querySelector("[type=submit]");
                if (submitButton) {
                    submitButton.disabled = true;
                    submitButton.setAttribute("aria-busy", "true");
                }
                try {
                    const response = await fetch(`/v1/api/instructions/${ticketTypeRoute}`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(chatMessage)
                    });

                    if (!response.ok) {
                        const errorData = await response.json();
                        throw new Error(errorData.message || "Failed to create ticket.");
                    }

                    const createdTicket = await response.json();

                    if (ticketsDataTable) {
                        await new Promise(resolve => {
                            ticketsDataTable.ajax.reload(() => resolve(), false);
                        });
                    }

                    await loadConversations();

                    showNotificationToast(`Ticket #${createdTicket.id} created successfully!`, 'success');

                    if (createTicketModal) createTicketModal.hide();
                    createTicketForm.reset();

                } catch (error) {
                    console.error("Error creating ticket:", error);
                    showNotificationToast(`Error: ${error.message}`, 'error');
                } finally {
                    isCreatingTicket = false;
                    if (submitButton) {
                        submitButton.disabled = false;
                        submitButton.removeAttribute("aria-busy");
                    }
                }
            });
        }

        if (createInquiryForm) {
            createInquiryForm.addEventListener("submit", async (e) => {
                e.preventDefault();
                if (isCreatingInquiry) return;

                const subjectSelect = document.getElementById("inquirySubject");
                const messageInput = document.getElementById("inquiryMessage");

                if (!subjectSelect) {
                    console.error("Could not find element with ID 'inquirySubject'.");
                    alert("An error occurred. Could not find the subject field.");
                    return;
                }

                const inquiryType = subjectSelect.value;
                const message = messageInput.value;

                let inquiryRoute;

                if (inquiryType === "Account Inquiry") {
                    inquiryRoute = "inquiry/accounts";
                } else if (inquiryType === "Sales and Management") {
                    inquiryRoute = "inquiry/sales";
                } else {
                    alert("Please select a valid inquiry type.");
                    return;
                }

                const chatMessage = {
                    Instruction: message,
                    InstructionId: null,
                };

                isCreatingInquiry = true;
                const submitButton = createInquiryForm.querySelector("[type=submit]");
                if (submitButton) {
                    submitButton.disabled = true;
                    submitButton.setAttribute("aria-busy", "true");
                }
                try {
                    const response = await fetch(`/v1/api/instructions/${inquiryRoute}`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(chatMessage)
                    });

                    if (!response.ok) {
                        const errorData = await response.json();
                        throw new Error(errorData.message || `Failed to create inquiry: ${response.statusText}`);
                    }

                    const createdInquiry = await response.json();

                    if (inquiriesDataTable) {
                        await new Promise(resolve => {
                            inquiriesDataTable.ajax.reload(() => resolve(), false);
                        });
                    }

                    await loadConversations();

                    showNotificationToast(`Inquiry #${createdInquiry.id} created successfully!`, 'success');

                    if (createInquiryModal) createInquiryModal.hide();
                    createInquiryForm.reset();

                } catch (error) {
                    console.error("Error creating inquiry:", error);
                    showNotificationToast(`Error: ${error.message}`, 'error');
                } finally {
                    isCreatingInquiry = false;
                    if (submitButton) {
                        submitButton.disabled = false;
                        submitButton.removeAttribute("aria-busy");
                    }
                }
            });
        }
    }

    messaging.on("message", ({ message: createdMessage, source }) => {
        const message = toLegacyMessage(createdMessage);
        const conversationId = Number(message.instructionId);
        const conversation = conversationsById.get(conversationId);
        if (conversation) {
            conversation.latestSequence = Math.max(
                Number(conversation.latestSequence) || 0,
                Number(message.sequence) || 0);
        }

        if (Number(currentChatContext.id) === conversationId) {
            displayMessage(message, false);
            advanceActiveReadCursor();
        } else if (conversation && source === "realtime") {
            conversation.unreadCount = (Number(conversation.unreadCount) || 0) + 1;
            setUnreadBadge(conversationId, conversation.unreadCount);
        }

        loadClientNotifications();
    });

    messaging.on("typing", typing => {
        if (String(typing.conversationId) !== String(currentChatContext.id)) return;
        const indicator = document.getElementById("typing-indicator");
        if (!indicator) return;
        indicator.textContent = typing.isTyping ? `${typing.displayName} is typing…` : "";
    });

    messaging.on("sendstate", ({ conversationId, clientMessageId, state }) => {
        if (state === "sent") {
            findPendingMessage(clientMessageId)?.remove();
            if (sendState && Number(currentChatContext.id) === Number(conversationId)) {
                sendState.textContent = "";
            }
            return;
        }
        renderPendingMessage(conversationId, clientMessageId, state);
    });

    messaging.on("conversationchanged", async change => {
        await loadConversations();
        if (Number(currentChatContext.id) === Number(change?.conversationId)
            && !conversationsById.has(Number(change?.conversationId))) {
            const removedConversationId = Number(currentChatContext.id);
            chatSwitchRequest += 1;
            messaging.cancelReconcile?.(removedConversationId);
            messaging.leave(removedConversationId).catch(() => {});
            currentChatContext = {};
            if (chatHeading) chatHeading.textContent = "Select a conversation";
            if (chatSubheading) chatSubheading.textContent = "Choose a chat to read messages.";
            setChatPanelState("This conversation is no longer active.", "empty");
            updateSendButtonState();
        }
    });

    messaging.on("notificationchanged", change => {
        updateClientNotificationBadge(change?.unreadCount || 0);
        loadClientNotifications();
        if (change?.notification?.message) {
            showNotificationToast(change.notification.message, 'info');
        }
    });

    messaging.on("state", ({ state }) => {
        const labels = {
            connected: "Connected",
            reconnecting: "Reconnecting…",
            reconnected: "Connection restored",
            disconnected: "Disconnected"
        };
        if (connectionStatus) {
            connectionStatus.textContent = labels[state] || "Connecting…";
            connectionStatus.dataset.state = state || "connecting";
        }
        updateSendButtonState();
        if (state === "reconnected") {
            loadConversations();
            advanceActiveReadCursor();
            window.setTimeout(() => {
                if (connectionStatus?.dataset.state === "reconnected") {
                    connectionStatus.textContent = "Connected";
                    connectionStatus.dataset.state = "connected";
                }
            }, 3000);
        }
    });

    connection.on("TicketStatusUpdated", (data) => {
        console.log("CLIENT: Ticket status updated:", data);

        if (ticketsDataTable) {
            ticketsDataTable.ajax.reload(null, false);
        }

        showNotificationToast(`Ticket #${data.ticketId} status updated to: ${data.newStatus}`, 'info');

        loadClientNotifications();
    });

    connection.on("InquiryStatusUpdated", (data) => {
        console.log("CLIENT: Inquiry status updated:", data);

        if (inquiriesDataTable) {
            inquiriesDataTable.ajax.reload(null, false); 
        }

        showNotificationToast(`Inquiry #${data.inquiryId} status updated to: ${data.newStatus}`, 'info');

        loadClientNotifications();
    });

    connection.on("NewTicketCreated", (data) => {
        console.log("CLIENT: New ticket created:", data);

        if (ticketsDataTable) {
            ticketsDataTable.ajax.reload(null, false);
        }

        loadConversations();
    });

    connection.on("NewInquiryCreated", (data) => {
        console.log("CLIENT: New inquiry created:", data);

        if (inquiriesDataTable) {
            inquiriesDataTable.ajax.reload(null, false);
        }

        loadConversations();
    });

    async function init() {
        document.querySelectorAll(".modal").forEach(wireModalFocus);

        try {
            await messaging.start();
            if (connectionStatus) {
                connectionStatus.textContent = "Connected";
                connectionStatus.dataset.state = "connected";
            }
        } catch (err) {
            console.error("Initialization Error: ", err);
            if (connectionStatus) {
                connectionStatus.textContent = "Disconnected";
                connectionStatus.dataset.state = "disconnected";
            }
        }
        updateSendButtonState();
        await loadConversations();
        initializeClientNotifications();

        if (supportTicketsTableE1.length) {
            ticketsDataTable = supportTicketsTableE1.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/tickets",
                    "dataSrc": function (json) {
                        console.log("DEBUG: Ticket data structure:", json.data[0]);
                        return json.data;
                    }
                },
                "columns": [
                    {
                        "data": "id",
                        "title": '<i class="bi bi-hash me-1"></i>ID',
                        "width": "8%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#${data}</span>`;
                        }
                    },
                    {
                        "data": "subject",
                        "title": '<i class="bi bi-ticket-perforated me-1"></i>Subject',
                        "width": "30%",
                        "className": "fw-semibold",
                        "render": function (data, type, row) {
                            const subject = data || 'General Support';
                            const truncated = subject.length > 40 ? subject.substring(0, 40) + '...' : subject;
                            return `<span title="${escapeHtml(subject)}" class="text-primary">${escapeHtml(truncated)}</span>`;
                        }
                    },
                    {
                        "data": "date",
                        "title": '<i class="bi bi-calendar3 me-1"></i>Date',
                        "width": "15%",
                        "className": "text-center",
                        "render": function (data) {
                            const date = new Date(data);
                            const formatted = date.toLocaleDateString('en-US', {
                                month: 'short',
                                day: 'numeric',
                                year: 'numeric'
                            });
                            const time = date.toLocaleTimeString('en-US', {
                                hour: '2-digit',
                                minute: '2-digit'
                            });
                            return `<div class="text-muted small">${formatted}</div><div class="text-secondary" style="font-size: 0.75rem">${time}</div>`;
                        }

                    },
                    {
                        "data": "status",
                        "title": '<i class="bi bi-info-circle me-1"></i>Status',
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data) {
                            const status = data || 'Pending';
                            const statusClass = `badge-status-${status.toLowerCase()}`;
                            return `<span class="badge ${statusClass}"><i class="bi bi-circle-fill me-1" style="font-size: 0.5rem"></i>${escapeHtml(status)}</span>`;
                        }
                    },
                    {
                        "data": "priority",
                        "title": '<i class="bi bi-exclamation-triangle me-1"></i>Priority',
                        "width": "12%",
                        "className": "text-center",
                        "render": (data) => generatePriorityBadge(data)
                    },
                    {
                        "data": null,
                        "title": '<i class="bi bi-gear me-1"></i>Actions',
                        "orderable": false,
                        "width": "15%",
                        "className": "text-center",
                        "render": function (data, type, row) {
                            const rowId = escapeHtml(row.id);
                            return `
                        <div class="action-buttons">
                            <button type="button" id="view-ticket-details-${rowId}" class="btn-icon-action view-details-btn" title="View Details" aria-label="View ticket #${rowId} details" data-bs-toggle="tooltip">
                                <i class="bi bi-eye"></i>
                            </button>
                            <button type="button" id="open-ticket-chat-${rowId}" class="btn-icon-action start-chat-btn" title="Open Chat" aria-label="Open chat for ticket #${rowId}" data-bs-toggle="tooltip">
                                <i class="bi bi-chat-square-text"></i>
                            </button>
                        </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "lengthMenu": [[5, 10, 25, 50], [5, 10, 25, 50]],
                "language": {
                    "emptyTable": '<div class="text-center p-4"><i class="bi bi-ticket-perforated fs-1 text-muted mb-3"></i><br><span class="text-muted">You haven\'t created any support tickets yet.</span><br><small class="text-secondary">Click "New Support Ticket" to get started!</small></div>',
                    "search": '<i class="bi bi-search me-2"></i>',
                    "lengthMenu": 'Show _MENU_ tickets',
                    "info": 'Showing _START_ to _END_ of _TOTAL_ tickets',
                    "infoEmpty": 'No tickets available',
                    "paginate": {
                        "first": '<i class="bi bi-chevron-bar-left"></i>',
                        "last": '<i class="bi bi-chevron-bar-right"></i>',
                        "next": '<i class="bi bi-chevron-right"></i>',
                        "previous": '<i class="bi bi-chevron-left"></i>'
                    }
                },
                "dom": '<"row"<"col-sm-12 col-md-6"l><"col-sm-12 col-md-6"f>>rt<"row"<"col-sm-12 col-md-5"i><"col-sm-12 col-md-7"p>>',
                "responsive": true,
                "processing": true,
                "deferRender": true,
                "stateSave": true,
                "drawCallback": function () {
                    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
                    tooltipTriggerList.map(function (tooltipTriggerEl) {
                        return new bootstrap.Tooltip(tooltipTriggerEl, {
                            trigger: 'hover',
                            delay: { show: 300, hide: 100 }
                        });
                    });
                }
            });

            const searchBox = supportTicketsTableE1.closest('.dataTables_wrapper').find('.dataTables_filter input');
            searchBox.attr('placeholder', 'Search tickets...').addClass('form-control-sm');
        }

        if (inquiriesTableE1.length) {
            inquiriesDataTable = inquiriesTableE1.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/inquiries",
                    "dataSrc": function (json) {
                        console.log("DEBUG: Inquiry data structure:", json.data[0]);
                        console.log("DEBUG: All inquiry fields:", json.data.length > 0 ? Object.keys(json.data[0]) : "No data");
                        return json.data;
                    }
                },
                "columns": [
                    {
                        "data": "id",
                        "title": '<i class="bi bi-hash me-1"></i>ID',
                        "width": "8%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#INQ-${data}</span>`;
                        }
                    },
                    {
                        "data": "topic",
                        "title": '<i class="bi bi-question-circle me-1"></i>Topic',
                        "width": "25%",
                        "className": "fw-semibold text-primary",
                        "render": function (data) {
                            return escapeHtml(data || 'General Inquiry');
                        }
                    },
                    {
                        "data": "inquiredBy",
                        "title": '<i class="bi bi-person me-1"></i>Inquired By',
                        "width": "20%",
                        "render": function (data) {
                            return escapeHtml(data || 'Unknown');
                        }
                    },
                    {
                        "data": "date",
                        "title": '<i class="bi bi-calendar3 me-1"></i>Date',
                        "width": "15%",
                        "className": "text-center",
                        "render": function (data) {
                            const date = new Date(data);
                            const formatted = date.toLocaleDateString('en-US', {
                                month: 'short',
                                day: 'numeric',
                                year: 'numeric'
                            });
                            const time = date.toLocaleTimeString('en-US', {
                                hour: '2-digit',
                                minute: '2-digit'
                            });
                            return `<div class="text-muted small">${formatted}</div><div class="text-secondary" style="font-size: 0.75rem">${time}</div>`;
                        }
                    },
                    {
                        "data": "outcome",
                        "title": '<i class="bi bi-info-circle me-1"></i>Outcome',
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data, type, row) {
                            const outcome = data || row.outcome || 'Pending';
                            const mappedOutcome = outcome === 'Completed' ? 'Resolved' : outcome;
                            const outcomeClass = `badge-status-${mappedOutcome.toLowerCase()}`;
                            return `<span class="badge ${outcomeClass}"><i class="bi bi-circle-fill me-1" style="font-size: 0.5rem"></i>${escapeHtml(outcome)}</span>`;
                        }
                    },
                    {
                        "data": null,
                        "title": '<i class="bi bi-gear me-1"></i>Actions',
                        "orderable": false,
                        "width": "20%",
                        "className": "text-center",
                        "render": function (data, type, row) {
                            const rowId = escapeHtml(row.id);
                            return `
                    <div class="action-buttons">
                        <button type="button" id="view-inquiry-details-${rowId}" class="btn-icon-action view-details-btn" title="View Details" aria-label="View inquiry #${rowId} details" data-bs-toggle="tooltip">
                            <i class="bi bi-eye"></i>
                        </button>
                        <button type="button" id="open-inquiry-chat-${rowId}" class="btn-icon-action start-chat-btn" title="Open Chat" aria-label="Open chat for inquiry #${rowId}" data-bs-toggle="tooltip">
                            <i class="bi bi-chat-square-text"></i>
                        </button>
                    </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "lengthMenu": [[5, 10, 25, 50], [5, 10, 25, 50]],
                "language": {
                    "emptyTable": '<div class="text-center p-4"><i class="bi bi-question-circle fs-1 text-muted mb-3"></i><br><span class="text-muted">No inquiries found.</span><br><small class="text-secondary">Click "New Inquiry" to submit your first inquiry!</small></div>',
                    "search": '<i class="bi bi-search me-2"></i>',
                    "lengthMenu": 'Show _MENU_ inquiries',
                    "info": 'Showing _START_ to _END_ of _TOTAL_ inquiries',
                    "infoEmpty": 'No inquiries available',
                    "paginate": {
                        "first": '<i class="bi bi-chevron-bar-left"></i>',
                        "last": '<i class="bi bi-chevron-bar-right"></i>',
                        "next": '<i class="bi bi-chevron-right"></i>',
                        "previous": '<i class="bi bi-chevron-left"></i>'
                    }
                },
                "dom": '<"row"<"col-sm-12 col-md-6"l><"col-sm-12 col-md-6"f>>rt<"row"<"col-sm-12 col-md-5"i><"col-sm-12 col-md-7"p>>',
                "responsive": true,
                "processing": false,
                "deferRender": true,
                "stateSave": true,
                "drawCallback": function () {
                    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
                    tooltipTriggerList.map(function (tooltipTriggerEl) {
                        return new bootstrap.Tooltip(tooltipTriggerEl, {
                            trigger: 'hover',
                            delay: { show: 300, hide: 100 }
                        });
                    });
                }
            });

            const inquirySearchBox = inquiriesTableE1.closest('.dataTables_wrapper').find('.dataTables_filter input');
            inquirySearchBox.attr('placeholder', 'Search inquiries...').addClass('form-control-sm');
        }

        initializeTicketSystem();

        const conversationListPanel = document.getElementById("conversation-list-panel");
        if (conversationListPanel) {
            conversationListPanel.addEventListener('click', async (e) => {
                const conversationItem = e.target.closest('.conversation-item');
                if (!conversationItem) return;
                e.preventDefault();
                try {
                    if (conversationItem.dataset.action === "start-group") {
                        conversationItem.disabled = true;
                        const conversation = await messaging.getOrCreateGroup();
                        await loadConversations();
                        await switchChatContext(conversation);
                        return;
                    }
                    const conversation = conversationsById.get(Number(conversationItem.dataset.id));
                    if (conversation) await switchChatContext(conversation);
                } catch (error) {
                    console.error("Unable to open conversation:", error);
                    showNotificationToast("Unable to open the conversation. Please retry.", "error");
                } finally {
                    conversationItem.disabled = false;
                }
            });
        }

        if (sendButton) sendButton.addEventListener("click", () => sendMessage());
        chatPanelBody?.addEventListener("click", event => {
            const retry = event.target.closest(".retry-message");
            if (retry) sendMessage(retry.dataset.clientMessageId);
        });
        if (messageInput) {
            messageInput.addEventListener("input", () => {
                updateSendButtonState();
                if (!currentChatContext.id) return;
                messaging.store.saveDraft(currentChatContext.id, messageInput.value);
                messaging.setTyping(currentChatContext.id, true).catch(() => {});
                clearTimeout(typingTimer);
                typingTimer = setTimeout(() => {
                    messaging.setTyping(currentChatContext.id, false).catch(() => {});
                }, 1500);
            });
            messageInput.addEventListener("keydown", (e) => {
                updateSendButtonState();
                if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendMessage(); }
            });
        }

        newPrivateChatModalElement?.addEventListener("show.bs.modal", loadAvailableAdmins);
        availableAdminsSearch?.addEventListener("input", filterAvailableAdmins);
        conversationSearch?.addEventListener("input", applyConversationFilters);
        conversationKindFilter?.addEventListener("change", applyConversationFilters);
        availableAdminsList?.addEventListener("click", event => {
            const adminButton = event.target.closest(".client-admin-option");
            if (adminButton) createPrivateConversation(adminButton);
        });
        mobileBackButton?.addEventListener("click", () => {
            void attachmentComposer?.resetForConversation();
            document.querySelector(".dashboard-container")?.classList.remove("client-chat-detail-open");
            const selectedItem = conversationList?.querySelector(".conversation-item.active");
            selectedItem?.focus();
        });
        document.addEventListener("visibilitychange", () => {
            if (document.visibilityState === "visible") advanceActiveReadCursor();
        });

        if (ticketsDataTable) {
            supportTicketsTableE1.on('click', '.start-chat-btn', function () {
                const rowData = ticketsDataTable.row($(this).parents('tr')).data();
                if (!rowData) {
                    console.error('No row data found for the clicked button.');
                    return
                };

                const route = `ticket/${rowData.subject.toLowerCase().replace(/\s+/g, '-')}`;

                switchChatContext({
                    id: rowData.id,
                    name: `#${rowData.id} - ${rowData.subject}`,
                    route: route
                });
            });

            supportTicketsTableE1.on('click', '.view-details-btn', function () {
                const rowData = ticketsDataTable.row($(this).parents('tr')).data();
                if (!rowData) {
                    console.error('No row data found for view details button');
                    return;
                }

                console.log('Row data for modal:', rowData);

                currentTicketData = rowData;

                const modalElement = document.getElementById("viewTicketDetailsModal");
                if (!modalElement) return;

                modalOpeners.set(modalElement, this);
                populateTicketDetailsModal(rowData, modalElement);
                bootstrap.Modal.getOrCreateInstance(modalElement).show(this);
            });
        }

        if (inquiriesDataTable) {
            inquiriesTableE1.on('click', '.start-chat-btn', function () {
                const rowData = inquiriesDataTable.row($(this).parents('tr')).data();
                if (!rowData) return;

                const route = `inquiry/${rowData.topic.toLowerCase().replace(/\s+/g, '-')}`;

                switchChatContext({
                    id: rowData.id,
                    name: `#${rowData.id} - ${rowData.topic}`,
                    route: route
                });
            });

            inquiriesTableE1.on('click', '.view-details-btn', function () {
                const rowData = inquiriesDataTable.row($(this).parents('tr')).data();
                if (!rowData) {
                    console.error('No row data found for inquiry view details button');
                    return;
                }

                console.log('Inquiry row data for modal:', rowData);

                const modalElement = document.getElementById("viewInquiryDetailsModal");
                if (!modalElement) return;

                modalOpeners.set(modalElement, this);
                populateInquiryDetailsModal(rowData, modalElement);
                bootstrap.Modal.getOrCreateInstance(modalElement).show(this);
            });
        }
    }

    init();
});
