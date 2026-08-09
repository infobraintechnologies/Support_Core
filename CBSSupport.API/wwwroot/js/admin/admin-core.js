"use strict";

window.AdminCore = (() => {

    let currentUser = { name: "Admin", id: null };
    let currentClientId = null;
    let connection = null;
    let clients = [];

    const pageInitializers = {
        dashboard: async function () {
            console.log("🎯 Loading Enhanced Dashboard...");
            try {
                if (window.AdminDashboard) {
                    await window.AdminDashboard.loadEnhancedDashboardData(currentClientId);
                }
            } catch (error) {
                console.error("Dashboard initialization error:", error);
                AdminUtils.showNotification('Failed to initialize dashboard', 'error');
            }
        },

        clients: function () {
            const searchInput = document.getElementById('client-directory-search');
            renderClientDirectory(searchInput?.value || '');
        },

        chats: async function () {
            console.log("💬 Initializing chats page for client:", currentClientId);

            if (window.AdminChat) {
                await window.AdminChat.initializeChatsPage(currentClientId);
            }
        },

        'ticket-management': function () {
            console.log("🎫 Initializing ticket management...");
            if (window.AdminTickets) {
                window.AdminTickets.initialize();
            }
        },

        'inquiry-management': function () {
            console.log("❓ Initializing inquiry management...");
            if (window.AdminInquiries) {
                window.AdminInquiries.initialize();
            }
        }
    };

    function populateClientSwitchers(items) {
        document.querySelectorAll('.client-switcher').forEach(select => {
            const allClientsOption = document.createElement('option');
            allClientsOption.value = '';
            allClientsOption.textContent = 'All clients';

            const options = items.map(client => {
                const option = document.createElement('option');
                option.value = String(client.id);
                option.textContent = client.name || `Client ${client.id}`;
                return option;
            });

            select.replaceChildren(allClientsOption, ...options);
            select.value = currentClientId || '';
        });
    }

    function renderClientDirectory(query = '') {
        const body = document.getElementById('client-directory-body');
        const state = document.getElementById('client-directory-state');
        const summary = document.getElementById('client-directory-summary');
        if (!body || !state || !summary) return;

        const normalizedQuery = query.trim().toLocaleLowerCase();
        const filteredClients = clients.filter(client =>
            (client.name || '').toLocaleLowerCase().includes(normalizedQuery)
            || String(client.id).includes(normalizedQuery));

        body.replaceChildren();
        state.replaceChildren();
        state.hidden = true;
        summary.textContent = `${filteredClients.length} of ${clients.length} clients shown`;

        if (filteredClients.length === 0) {
            const heading = document.createElement('h3');
            heading.textContent = clients.length === 0 ? 'No clients available' : 'No matching clients';
            const message = document.createElement('p');
            message.textContent = clients.length === 0
                ? 'The client directory did not return any accounts.'
                : 'Try a different client name or identifier.';
            state.append(heading, message);
            state.hidden = false;
            return;
        }

        filteredClients.forEach(client => {
            const row = document.createElement('tr');

            const nameCell = document.createElement('th');
            nameCell.scope = 'row';
            const name = document.createElement('span');
            name.className = 'client-directory-name';
            name.textContent = client.name || `Client ${client.id}`;
            const description = document.createElement('span');
            description.className = 'client-directory-description';
            description.textContent = 'Support account';
            nameCell.append(name, description);

            const idCell = document.createElement('td');
            const id = document.createElement('code');
            id.textContent = String(client.id);
            idCell.append(id);

            const actionCell = document.createElement('td');
            actionCell.className = 'text-end';
            const action = document.createElement('button');
            action.type = 'button';
            action.className = 'btn btn-sm btn-outline-primary client-directory-open';
            action.dataset.clientId = String(client.id);
            action.textContent = 'Open workspace';
            actionCell.append(action);

            row.append(nameCell, idCell, actionCell);
            body.append(row);
        });
    }

    function renderClientDirectoryError() {
        const body = document.getElementById('client-directory-body');
        const state = document.getElementById('client-directory-state');
        const summary = document.getElementById('client-directory-summary');
        if (!body || !state || !summary) return;

        body.replaceChildren();
        summary.textContent = 'Client directory unavailable';
        const heading = document.createElement('h3');
        heading.textContent = 'Could not load clients';
        const message = document.createElement('p');
        message.textContent = 'Check the connection and try again.';
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.className = 'btn btn-sm btn-outline-primary';
        retry.id = 'retry-client-directory';
        retry.textContent = 'Try again';
        state.replaceChildren(heading, message, retry);
        state.hidden = false;
    }

    async function loadClients() {
        try {
            const clientsResp = await fetch('/v1/api/clients');
            if (!clientsResp.ok) {
                throw new Error(`Client directory request failed with status ${clientsResp.status}.`);
            }

            const result = await clientsResp.json();
            clients = Array.isArray(result) ? result : [];
            if (currentClientId == null) currentClientId = '';
            populateClientSwitchers(clients);
            if (!currentClientId) $('#admin-tenant-context').text('Scope: All clients');
            renderClientDirectory(document.getElementById('client-directory-search')?.value || '');
            console.log("✅ Client directory loaded");
        } catch (error) {
            console.error("❌ Error loading clients:", error);
            renderClientDirectoryError();
        }
    }

    function handleClientChange() {
        $('.client-switcher').on('change', function () {
            const selectedClientId = $(this).val();
            const selectedClientName = $(this).find('option:selected').text();
            currentClientId = selectedClientId;

            $('.client-switcher').val(selectedClientId);
            $('#admin-tenant-context').text(
                selectedClientId ? `Scope: ${selectedClientName}` : 'Scope: All clients');

            const activePage = $('.admin-sidebar .nav-link.active').data('page');

            if (activePage === 'dashboard' || activePage === 'chats') {
                if (pageInitializers[activePage]) {
                    pageInitializers[activePage]();
                }
            }

            if (window.AdminTickets && window.AdminTickets.getTicketsTable) {
                const ticketsTable = window.AdminTickets.getTicketsTable();
                if (ticketsTable) {
                    const clientName = $(this).find('option:selected').text();
                    const searchTerm = selectedClientId ? `^${clientName}$` : '';
                    ticketsTable.column(1).search(searchTerm, true, false).draw();
                }
            }

            if (window.AdminInquiries && window.AdminInquiries.getInquiriesTable) {
                const inquiriesTable = window.AdminInquiries.getInquiriesTable();
                if (inquiriesTable) {
                    const clientName = $(this).find('option:selected').text();
                    const searchTerm = selectedClientId ? `^${clientName}$` : '';
                    inquiriesTable.column(1).search(searchTerm, true, false).draw();
                }
            }
        });
    }

    function initializeEnhancedEventHandlers() {
        $(document).on('input', '#client-directory-search', function () {
            renderClientDirectory(this.value);
        });

        $(document).on('click', '#retry-client-directory', function () {
            loadClients();
        });

        $(document).on('click', '.client-directory-open', function () {
            const clientId = this.dataset.clientId;
            const clientSwitcher = $('.client-switcher').first();
            clientSwitcher.val(String(clientId)).trigger('change');
            if (window.AdminNavigation) {
                window.AdminNavigation.navigateToDashboard();
            }
        });

        $(document).on('click', '#refresh-dashboard-btn', function () {
            $(this).html('<i class="fas fa-spinner fa-spin me-1"></i>Refreshing...');
            if (window.AdminDashboard) {
                window.AdminDashboard.loadEnhancedDashboardData(currentClientId).finally(() => {
                    $(this).html('<i class="fas fa-sync-alt me-1"></i>Refresh');
                });
            }
        });

        $(document).on('click', '#btn-update-ticket', function (e) {
            e.preventDefault();
            if (window.AdminTickets && window.AdminTickets.updateTicketStatus) {
                window.AdminTickets.updateTicketStatus();
            }
        });

        $(document).on('click', '#btn-update-inquiry', function () {
            if (window.AdminInquiries && window.AdminInquiries.updateInquiryStatus) {
                window.AdminInquiries.updateInquiryStatus();
            }
        });

        $(document).on('click', '#btn-close-ticket-detail', function () {
            if (window.AdminTickets && window.AdminTickets.closeTicketDetail) {
                window.AdminTickets.closeTicketDetail();
            }
        });

        $(document).on('click', '#btn-close-inquiry-detail', function () {
            if (window.AdminInquiries && window.AdminInquiries.closeInquiryDetail) {
                window.AdminInquiries.closeInquiryDetail();
            }
        });

        $(document).on('click', '#btn-start-ticket-chat', function () {
            if (window.AdminTickets && window.AdminTickets.startTicketChat) {
                window.AdminTickets.startTicketChat();
            }
        });

        $(document).on('click', '#btn-start-inquiry-chat', function () {
            if (window.AdminInquiries && window.AdminInquiries.startInquiryChat) {
                window.AdminInquiries.startInquiryChat();
            }
        });

        console.log("AdminCore: Enhanced event handlers initialized");
    }

    async function initialize() {
        console.log("🚀 AdminCore: Starting initialization...");

        try {
            try {
                const meResp = await fetch('/v1/api/accounts/me');
                if (meResp.ok) {
                    currentUser = await meResp.json();
                    console.log("✅ Current user loaded:", currentUser);
                    $('#admin-username-display').text(currentUser.name);
                }
            } catch (error) {
                console.warn("⚠️ Could not load user info:", error);
            }

            if (window.AdminSignalR) {
                connection = await window.AdminSignalR.initialize();
                console.log("✅ SignalR connection established");
            }

            await loadClients();

            if (window.AdminNavigation) {
                window.AdminNavigation.initializeNavigationEvents();
                console.log("✅ Navigation module initialized");
            }

            if (window.AdminNotifications) {
                window.AdminNotifications.initialize();
                console.log("✅ Notifications module initialized");
            }

            if (window.AdminChat) {
                window.AdminChat.initializeChatSidebar();
                console.log("✅ Chat module initialized");
            }

            handleClientChange();

            initializeEnhancedEventHandlers();

            if (pageInitializers.dashboard) {
                await pageInitializers.dashboard();
                console.log("✅ Dashboard initialized");
            }

            if ('Notification' in window && Notification.permission === 'default') {
                await Notification.requestPermission();
            }

            console.log("🎉 AdminCore: Initialization completed successfully!");

        } catch (error) {
            console.error("❌ AdminCore: Initialization failed:", error);
            AdminUtils.showNotification('Failed to initialize admin panel. Please refresh the page.', 'error');
        }
    }

    return {
        initialize,
        getCurrentUser: () => currentUser,
        getCurrentClientId: () => currentClientId,
        setCurrentClientId: (clientId) => { currentClientId = clientId; },
        getConnection: () => connection,
        getPageInitializers: () => pageInitializers
    };
})();

document.addEventListener('DOMContentLoaded', () => {
    window.AdminCore.initialize();
});
