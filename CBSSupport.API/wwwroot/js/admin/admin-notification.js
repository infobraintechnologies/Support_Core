"use strict";

window.AdminNotifications = (() => {

    let unreadNotificationCount = 0;
    let notificationPollingInterval = null;

    function getNotificationIcon(type) {
        const icons = {
            'ticket': 'bi bi-ticket-perforated',
            'inquiry': 'bi bi-question-circle',
            'message': 'bi bi-chat',
            'status_change': 'bi bi-arrow-left-right'
        };
        return icons[type] || 'bi bi-bell';
    }

    async function loadNotifications() {
        try {
            const response = await fetch('/api/v1/notifications?limit=20', { credentials: 'same-origin' });
            if (!response.ok) throw new Error('Failed to load notifications');

            const page = await response.json();
            const allNotifications = processNotifications(page.items || []);

            updateNotificationBadge(page.unreadCount || 0);
            renderNotifications(allNotifications);

            return allNotifications;
        } catch (error) {
            console.error('Error loading notifications:', error);
            renderNotificationLoadError();
            return [];
        }
    }

    function processNotifications(notificationRows) {
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

            notification.timeAgo = AdminUtils.getTimeAgo(notification.createdAt);
            notification.icon = getNotificationIcon(notification.type);
            notifications.push(notification);
        });

        notifications.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
        return notifications;
    }

    function updateNotificationBadge(count) {
        const badges = [
            'admin-notification-count',
            'admin-notification-count-clients',
            'admin-notification-count-chats',
            'admin-notification-count-tickets',
            'admin-notification-count-inquiries'
        ];

        badges.forEach(badgeId => {
            const badge = document.getElementById(badgeId);
            if (badge) {
                if (count > 0) {
                    badge.textContent = count > 99 ? '99+' : count.toString();
                    badge.style.display = 'block';
                } else {
                    badge.style.display = 'none';
                }
            }
        });

        unreadNotificationCount = count;
    }

    function renderNotifications(notifications) {
        let container = document.getElementById('admin-notification-list') ||
            document.getElementById('dynamic-notification-list');

        if (!container) {
            return;
        }

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

    function renderNotificationLoadError() {
        const container = document.getElementById('admin-notification-list') ||
            document.getElementById('dynamic-notification-list');
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
        retry.addEventListener('click', loadNotifications, { once: true });
        state.append(message, retry);
        container.replaceChildren(state);
    }

    async function markNotificationAsRead(notificationId) {
        try {
            const response = await fetch(`/api/v1/notifications/${notificationId}/read`, {
                method: 'PUT',
                headers: requestHeaders(),
                credentials: 'same-origin'
            });

            if (response.ok) {
                const notificationElement = document.querySelector(`[data-id="${notificationId}"]`);
                if (notificationElement && notificationElement.classList.contains('unread')) {
                    notificationElement.classList.remove('unread');
                    notificationElement.querySelector('.notification-unread-label')?.remove();
                }
                const changed = await response.json();
                updateNotificationBadge(changed.unreadCount || 0);
            }
        } catch (error) {
            console.error('Error marking notification as read:', error);
        }
    }

    async function markAllNotificationsAsRead() {
        try {
            const response = await fetch('/api/v1/notifications/read-all', {
                method: 'PUT',
                headers: requestHeaders(),
                credentials: 'same-origin'
            });

            if (response.ok) {
                document.querySelectorAll('.notification-item.unread').forEach(item => {
                    item.classList.remove('unread');
                    item.querySelector('.notification-unread-label')?.remove();
                });

                const result = await response.json();
                updateNotificationBadge(result.unreadCount || 0);
                AdminUtils.showNotification('All notifications marked as read', 'success');
            }
        } catch (error) {
            console.error('Error marking all notifications as read:', error);
            AdminUtils.showNotification('Failed to mark notifications as read', 'error');
        }
    }

    function requestHeaders() {
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        return token ? { 'RequestVerificationToken': token } : {};
    }

    function createNotificationMenu() {
        const menu = document.createElement('div');
        menu.className = 'header-notification-dropdown-menu';
        menu.id = 'dynamic-notification-menu';
        menu.setAttribute('role', 'region');
        menu.setAttribute('aria-label', 'Notifications');

        menu.innerHTML = `
            <div class="notification-header">
                <h6>Notifications</h6>
                <button type="button" class="btn btn-sm btn-link mark-all-read-btn">Mark all as read</button>
            </div>
            <div class="notification-list" id="dynamic-notification-list" aria-live="polite">
                <div class="notification-loading">
                    <div class="spinner-border spinner-border-sm" aria-hidden="true"></div>
                    <span>Loading notifications…</span>
                </div>
            </div>
        `;

        setupNotificationMenuEvents(menu);
        return menu;
    }

    function setupNotificationMenuEvents(menu) {
        const markAllBtn = menu.querySelector('.mark-all-read-btn');
        if (markAllBtn) {
            markAllBtn.addEventListener('click', markAllNotificationsAsRead);
        }

        menu.addEventListener('click', async (e) => {
            const notificationItem = e.target.closest('.notification-item');
            if (notificationItem) {
                const notificationId = notificationItem.dataset.id;
                const entityId = notificationItem.dataset.entityId;
                const entityType = notificationItem.dataset.entityType;

                if (notificationItem.classList.contains('unread')) {
                    await markNotificationAsRead(notificationId);
                }

                if (entityType === 'message') {
                    AdminNavigation.navigateToChatsPage();
                    setTimeout(async () => {
                        if (window.AdminChat && window.AdminChat.openChatConversation) {
                            await window.AdminChat.openChatConversation(entityId);
                        }
                    }, 500);
                } else if (entityType === 'ticket' && entityId) {
                    AdminNavigation.navigateToTicketManagement();
                    setTimeout(() => {
                        if (window.AdminTickets && window.AdminTickets.loadTicketDetails) {
                            window.AdminTickets.loadTicketDetails(entityId);
                        }
                    }, 500);
                } else if (entityType === 'inquiry' && entityId) {
                    AdminNavigation.navigateToInquiryManagement();
                    setTimeout(() => {
                        if (window.AdminInquiries && window.AdminInquiries.loadInquiryDetails) {
                            window.AdminInquiries.loadInquiryDetails(entityId);
                        }
                    }, 500);
                }

                menu.remove();
            }
        });

        menu.addEventListener('keydown', e => {
            if (e.key === 'Escape') {
                const trigger = document.querySelector('.header-notification-btn[aria-expanded="true"]');
                menu.remove();
                trigger?.setAttribute('aria-expanded', 'false');
                trigger?.focus();
            }
        });
    }

    function initialize() {
        const buttonIds = [
            'admin-notification-btn',
            'admin-notification-btn-clients',
            'admin-notification-btn-chats',
            'admin-notification-btn-tickets',
            'admin-notification-btn-inquiries'
        ];

        buttonIds.forEach(btnId => {
            const btn = document.getElementById(btnId);

            if (btn) {
                btn.replaceWith(btn.cloneNode(true));
                const newBtn = document.getElementById(btnId);
                newBtn.setAttribute('aria-expanded', 'false');
                newBtn.setAttribute('aria-haspopup', 'true');
                newBtn.setAttribute('aria-controls', 'dynamic-notification-menu');

                newBtn.addEventListener('click', async (e) => {
                    e.preventDefault();
                    e.stopPropagation();

                    document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                        menu.remove();
                    });
                    document.querySelectorAll('.header-notification-btn').forEach(button =>
                        button.setAttribute('aria-expanded', 'false'));

                    const notificationMenu = createNotificationMenu();

                    if (btnId === 'admin-notification-btn') {
                        const container = newBtn.closest('.header-notification-container');
                        container.appendChild(notificationMenu);
                    } else {
                        document.body.appendChild(notificationMenu);
                        const rect = newBtn.getBoundingClientRect();
                        notificationMenu.style.position = 'fixed';
                        notificationMenu.style.top = `${rect.bottom + 8}px`;
                        notificationMenu.style.right = `${window.innerWidth - rect.right}px`;
                        notificationMenu.style.left = 'auto';
                        notificationMenu.style.zIndex = '1060';
                    }

                    try {
                        await loadNotifications();
                        notificationMenu.classList.add('show');
                        newBtn.setAttribute('aria-expanded', 'true');
                        notificationMenu.querySelector('.notification-item, .mark-all-read-btn')?.focus();
                    } catch (error) {
                        console.error('❌ Error loading notifications:', error);
                    }
                });
            }
        });

        document.addEventListener('click', (e) => {
            if (!e.target.closest('.header-notification-container') &&
                !e.target.closest('.header-notification-btn') &&
                !e.target.closest('.header-notification-dropdown-menu')) {

                document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                    menu.remove();
                });
                document.querySelectorAll('.header-notification-btn').forEach(button =>
                    button.setAttribute('aria-expanded', 'false'));
            }
        });

        loadNotifications();

        if (notificationPollingInterval) {
            clearInterval(notificationPollingInterval);
        }
        notificationPollingInterval = setInterval(loadNotifications, 30000);
    }

    return {
        initialize,
        loadNotifications,
        markNotificationAsRead,
        markAllNotificationsAsRead,
        updateNotificationBadge
    };
})();
