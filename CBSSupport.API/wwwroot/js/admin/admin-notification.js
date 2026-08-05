/**
 * Admin Panel Notifications System
 * Handles all notification functionality including loading, rendering, and user interactions
 */
"use strict";

window.AdminNotifications = (() => {

    // ============================================
    // 🔔 STATE MANAGEMENT
    // ============================================

    let unreadNotificationCount = 0;
    let notificationPollingInterval = null;

    // ============================================
    // 🔔 NOTIFICATION PROCESSING
    // ============================================

    function getNotificationIcon(type) {
        const icons = {
            'ticket': 'fas fa-ticket-alt',
            'inquiry': 'fas fa-question-circle',
            'message': 'fas fa-comment',
            'status_change': 'fas fa-exchange-alt'
        };
        return icons[type] || 'fas fa-bell';
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

                    if (count > unreadNotificationCount) {
                        const btn = badge.closest('.header-notification-btn');
                        if (btn) {
                            btn.classList.add('notification-shake');
                            setTimeout(() => btn.classList.remove('notification-shake'), 500);
                        }
                    }
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
            icon.className = 'fas fa-bell-slash fa-2x mb-2';
            const text = document.createElement('p');
            text.textContent = 'No notifications yet';
            empty.append(icon, text);
            container.appendChild(empty);
            return;
        }

        notifications.forEach(notification => {
            const item = document.createElement('div');
            item.className = `notification-item${notification.isRead ? '' : ' unread'}`;
            item.dataset.id = String(notification.id);
            item.dataset.entityId = String(notification.entityId);
            item.dataset.entityType = String(notification.entityType);

            const content = document.createElement('div');
            content.className = 'notification-content';

            const iconWrapper = document.createElement('div');
            iconWrapper.className = `notification-icon ${notification.type}`;
            const icon = document.createElement('i');
            icon.className = notification.icon;
            iconWrapper.appendChild(icon);

            const text = document.createElement('div');
            text.className = 'notification-text';

            const title = document.createElement('div');
            title.className = 'notification-title';
            title.textContent = notification.title;

            const message = document.createElement('div');
            message.className = 'notification-message';
            message.textContent = notification.message;

            const time = document.createElement('div');
            time.className = 'notification-time';
            time.textContent = notification.timeAgo;

            text.append(title, message, time);
            content.append(iconWrapper, text);
            item.appendChild(content);
            container.appendChild(item);
        });
    }

    // ============================================
    // 🔔 NOTIFICATION ACTIONS
    // ============================================

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

    // ============================================
    // 🔔 NOTIFICATION MENU MANAGEMENT
    // ============================================

    function createNotificationMenu() {
        const menu = document.createElement('div');
        menu.className = 'header-notification-dropdown-menu';
        menu.id = 'dynamic-notification-menu';

        menu.innerHTML = `
            <div class="notification-header">
                <h6>Notifications</h6>
                <button class="btn btn-sm btn-link mark-all-read-btn">Mark all as read</button>
            </div>
            <div class="notification-list" id="dynamic-notification-list">
                <div class="notification-loading">
                    <div class="spinner-border spinner-border-sm"></div>
                    <span>Loading notifications...</span>
                </div>
            </div>
            <div class="notification-footer">
                <a href="#" class="btn btn-sm btn-primary w-100">View All</a>
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

                // Navigate to relevant page
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
    }

    // ============================================
    // 🔔 INITIALIZATION
    // ============================================

    function initialize() {
        const buttonIds = [
            'admin-notification-btn',
            'admin-notification-btn-chats',
            'admin-notification-btn-tickets',
            'admin-notification-btn-inquiries'
        ];

        buttonIds.forEach(btnId => {
            const btn = document.getElementById(btnId);

            if (btn) {
                btn.replaceWith(btn.cloneNode(true));
                const newBtn = document.getElementById(btnId);

                newBtn.addEventListener('click', async (e) => {
                    e.preventDefault();
                    e.stopPropagation();

                    document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                        menu.remove();
                    });

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
                    } catch (error) {
                        console.error('❌ Error loading notifications:', error);
                    }
                });
            }
        });

        // Close dropdown when clicking outside
        document.addEventListener('click', (e) => {
            if (!e.target.closest('.header-notification-container') &&
                !e.target.closest('.header-notification-btn') &&
                !e.target.closest('.header-notification-dropdown-menu')) {

                document.querySelectorAll('.header-notification-dropdown-menu').forEach(menu => {
                    menu.remove();
                });
            }
        });

        // Load initial notifications and start polling
        loadNotifications();

        if (notificationPollingInterval) {
            clearInterval(notificationPollingInterval);
        }
        notificationPollingInterval = setInterval(loadNotifications, 30000);
    }

    // ============================================
    // 🔗 PUBLIC API
    // ============================================

    return {
        initialize,
        loadNotifications,
        markNotificationAsRead,
        markAllNotificationsAsRead,
        updateNotificationBadge
    };
})();
