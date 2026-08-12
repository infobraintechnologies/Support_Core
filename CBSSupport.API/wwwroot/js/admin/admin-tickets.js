"use strict";

window.AdminTickets = (() => {

    let currentTicketData = null;
    let ticketsTable = null;

    function initialize() {
        const ticketTable = $('#ticketsTable');
        if (ticketTable.length && !$.fn.DataTable.isDataTable('#ticketsTable')) {
            ticketsTable = ticketTable.DataTable({
                "ajax": {
                    "url": "/api/v1/admin/tickets",
                    "data": function (request) {
                        const clientId = window.AdminCore?.getCurrentClientId();
                        if (clientId) {
                            request.clientId = clientId;
                        }

                        const status = $('#status-filter-tickets').val();
                        if (status) {
                            request.status = status;
                        }
                    },
                    "dataSrc": "items"
                },
                "columns": [
                    {
                        "data": "id",
                        "title": "ID",
                        "width": "8%",
                        "className": "fw-medium",
                        "render": function (data) {
                            return `<span class="ticket-id">#${data}</span>`;
                        }
                    },
                    {
                        "data": "status",
                        "title": "Status",
                        "width": "13%",
                        "className": "text-center",
                        "render": function (data) {
                            return AdminUtils.generateStatusBadge(data);
                        }
                    },
                    {
                        "data": "priority",
                        "title": "Priority",
                        "width": "12%",
                        "className": "text-center",
                        "render": function (data) {
                            return AdminUtils.generatePriorityBadge(data);
                        }
                    },
                    {
                        "data": null,
                        "title": "Subject",
                        "width": "27%",
                        "render": function (data, type, row) {
                            const subject = AdminUtils.escapeHtml(row.subject || 'General Support');
                            return `<div class="ticket-subject">${subject}</div>`;
                        }
                    },
                    {
                        "data": "createdByName",
                        "title": "Requester",
                        "width": "15%",
                        "render": function (data) {
                            return AdminUtils.escapeHtml(data || 'Unknown');
                        }
                    },
                    {
                        "data": "createdAt",
                        "title": "Last updated",
                        "width": "15%",
                        "render": function (data, type) {
                            if (type !== 'display') return data || '';
                            if (!data) return '<span class="text-muted">Not available</span>';
                            const date = new Date(data);
                            if (Number.isNaN(date.getTime())) return '<span class="text-muted">Not available</span>';
                            return `<time datetime="${date.toISOString()}">${date.toLocaleString()}</time>`;
                        }
                    },
                    {
                        "data": null,
                        "title": "Actions",
                        "orderable": false,
                        "width": "11%",
                        "className": "text-center",
                        "render": function () {
                            return `
                                <div class="action-buttons">
                                    <button type="button" class="btn-icon-action view-ticket-details-btn" title="View details" aria-label="View ticket details">
                                        <i class="bi bi-eye" aria-hidden="true"></i>
                                    </button>
                                    <button type="button" class="btn-icon-action start-chat-btn" title="Open chat" aria-label="Open ticket conversation">
                                        <i class="bi bi-chat-square-text" aria-hidden="true"></i>
                                    </button>
                                </div>`;
                        }
                    }
                ],
                "order": [[0, 'desc']],
                "pageLength": 10,
                "responsive": true,
                "processing": true,
                "language": {
                    "emptyTable": "No tickets yet. New client submissions will show up here.",
                    "zeroRecords": "No tickets match your filters.",
                    "processing": "Loading tickets…",
                    "search": "Search tickets:",
                },
                "createdRow": function (row, data) {
                    $(row).addClass('clickable-ticket-row ticket-row');
                    $(row).attr('data-ticket-id', data.id);
                    $(row).attr('tabindex', '0');
                    $(row).attr('aria-label', `View ticket ${data.id}: ${data.subject || 'General Support'}`);
                }
            });

            setupTicketTableEvents();
            setupStatusFilter();
        }
    }

    function setupStatusFilter() {
        $('#status-filter-tickets')
            .off('change.adminTickets')
            .on('change.adminTickets', function () {
                filterByStatus($(this).val());
            });
    }

    function setupTicketTableEvents() {
        if (!ticketsTable) return;

        const ticketTable = $('#ticketsTable');

        ticketTable.on('click', '.view-ticket-details-btn', function (e) {
            e.stopPropagation();
            const rowData = ticketsTable.row($(this).parents('tr')).data();
            if (rowData) {
                loadTicketDetails(rowData.id);
            }
        });

        ticketTable.on('click', '.start-chat-btn', function (e) {
            e.stopPropagation();
            const data = ticketsTable.row($(this).parents('tr')).data();
            if (data && window.AdminChat) {
                window.AdminChat.openEnhancedFloatingChatBox(data, 'tkt');
            }
        });

        ticketTable.on('click', 'tbody tr', function (e) {
            if ($(e.target).closest('.action-buttons').length === 0) {
                const rowData = ticketsTable.row(this).data();
                if (rowData) {
                    loadTicketDetails(rowData.id);
                }
            }
        });

        ticketTable.on('keydown', 'tbody tr', function (e) {
            if ((e.key === 'Enter' || e.key === ' ') && $(e.target).closest('.action-buttons').length === 0) {
                e.preventDefault();
                const rowData = ticketsTable.row(this).data();
                if (rowData) {
                    loadTicketDetails(rowData.id);
                }
            }
        });
    }

    async function loadTicketDetails(ticketId) {
        try {
            const response = await fetch(`/api/v1/tickets/${ticketId}`);
            if (!response.ok) throw new Error('Failed to load ticket details');

            const ticket = await response.json();
            currentTicketData = ticket;

            console.log('🎫 AdminTickets: Loaded ticket data:', ticket);

            $('#detail-ticket-id').text(`#TKT-${ticket.id}`);
            $('#detail-ticket-subject').text(ticket.subject || 'General Support');
            $('#detail-ticket-status').val(ticket.status || 'Open');
            $('#detail-ticket-priority').html(AdminUtils.generatePriorityBadge(ticket.priority || 'Normal'));
            $('#detail-ticket-created-by').text(ticket.createdByName || 'Unknown');
            $('#detail-ticket-client').text(ticket.clientName || 'Unknown');
            $('#detail-ticket-date').text(ticket.createdAt
                ? new Date(ticket.createdAt).toLocaleString()
                : 'Not available');
            $('#detail-ticket-description').text(ticket.description || 'No description available.');
            $('#detail-ticket-resolved-by').text(ticket.resolvedByName || 'Not resolved');

            if (ticket.resolvedAt) {
                $('#detail-ticket-resolved-date').text(new Date(ticket.resolvedAt).toLocaleString());
            } else {
                $('#detail-ticket-resolved-date').text('Not resolved');
            }

            $('#ticket-detail-placeholder').hide();
            $('#ticket-detail-content').show();
            $('.ticket-properties').scrollTop(0);

            console.log('🎫 AdminTickets: Ticket details loaded successfully');

        } catch (error) {
            console.error('🎫 AdminTickets: Error loading ticket details:', error);
            AdminUtils.showNotification('Failed to load ticket details. Please try again.', 'error');
        }
    }

    async function updateTicketStatus() {
        if (!currentTicketData) {
            console.error('🎫 AdminTickets: No current ticket data for status update');
            AdminUtils.showNotification('No ticket selected for status update.', 'error');
            return;
        }

        const newStatus = $('#detail-ticket-status').val();
        const isCompleted = newStatus === 'Resolved';

        console.log(`🎫 AdminTickets: Updating ticket ${currentTicketData.id} to status: ${newStatus}`);

        try {
            const updateBtn = $('#btn-update-ticket');
            updateBtn.prop('disabled', true).html('<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Saving…');

            const response = await fetch(`/api/v1/admin/tickets/${currentTicketData.id}/status`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ status: newStatus, expectedVersion: currentTicketData.version })
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({ message: 'Unknown error' }));
                throw new Error(errorData.message || `HTTP ${response.status}: ${response.statusText}`);
            }

            const result = await response.json();
            console.log('🎫 AdminTickets: Update response:', result);

            if (result && result.id) {
                currentTicketData.version = result.version;
                const currentUser = window.AdminCore?.getCurrentUser();
                currentTicketData.status = newStatus;

                if (isCompleted) {
                    currentTicketData.resolvedAt = new Date().toISOString();
                    currentTicketData.resolvedByName = currentUser?.name || 'Admin';
                } else {
                    currentTicketData.resolvedAt = null;
                    currentTicketData.resolvedByName = null;
                }

                if (currentTicketData.resolvedAt) {
                    $('#detail-ticket-resolved-date').text(new Date(currentTicketData.resolvedAt).toLocaleString());
                    $('#detail-ticket-resolved-by').text(currentTicketData.resolvedByName);
                } else {
                    $('#detail-ticket-resolved-date').text('Not resolved');
                    $('#detail-ticket-resolved-by').text('Not resolved');
                }

                if (ticketsTable) {
                    ticketsTable.ajax.reload(null, false);
                }

                AdminUtils.showNotification(`Ticket status updated to ${newStatus}.`, 'success');
            } else {
                throw new Error(result.message || 'Update failed');
            }

        } catch (error) {
            console.error('🎫 AdminTickets: Error updating ticket status:', error);
            AdminUtils.showNotification(`Failed to update ticket status: ${error.message}`, 'error');
        } finally {
            const updateBtn = $('#btn-update-ticket');
            updateBtn.prop('disabled', false);
            updateBtn.html('<i class="bi bi-floppy" aria-hidden="true"></i> Save status');
        }
    }

    function closeTicketDetail() {
        currentTicketData = null;
        $('#ticket-detail-content').hide();
        $('#ticket-detail-placeholder').show();
    }

    function startTicketChat() {
        if (currentTicketData && window.AdminChat) {
            window.AdminChat.openEnhancedFloatingChatBox(currentTicketData, 'tkt');
        }
    }

    function filterByClient() {
        if (ticketsTable) {
            closeTicketDetail();
            ticketsTable.ajax.reload(null, true);
        }
    }

    function filterByStatus(status) {
        $('#status-filter-tickets').val(status || '');
        if (ticketsTable) {
            closeTicketDetail();
            ticketsTable.ajax.reload(null, true);
        }
    }

    async function navigateToTicketChat(ticketData) {
        console.log('🎫 AdminTickets: Navigating to ticket chat for:', ticketData);

        if (ticketData.clientName) {
            const clientOption = $('.client-switcher option').filter(function () {
                return $(this).text() === ticketData.clientName;
            });

            if (clientOption.length > 0) {
                const clientId = clientOption.val();
                if (window.AdminCore) {
                    window.AdminCore.setCurrentClientId(clientId);
                }
                $('.client-switcher').val(clientId);
            }
        }

        if (window.AdminNavigation) {
            window.AdminNavigation.navigateToChatsPage();
        }

        setTimeout(async () => {
            try {
                let conversationItem = $(`.admin-conversation-item[data-id="${ticketData.id}"]`);

                if (conversationItem.length === 0 && window.AdminChat) {
                    const currentClientId = window.AdminCore?.getCurrentClientId();
                    await window.AdminChat.refreshAdminConversations(currentClientId);

                    setTimeout(() => {
                        conversationItem = $(`.admin-conversation-item[data-id="${ticketData.id}"]`);

                        if (conversationItem.length > 0) {
                            conversationItem.click();
                            AdminUtils.showNotification(`Opened chat for Ticket #${ticketData.id}`, 'success');
                        } else {
                            AdminUtils.showNotification(`Chat conversation for Ticket #${ticketData.id} not found. Please use the floating chat instead.`, 'warning');
                        }
                    }, 1000);
                } else {
                    conversationItem.click();
                    AdminUtils.showNotification(`Opened chat for Ticket #${ticketData.id}`, 'success');
                }
            } catch (error) {
                console.error('🎫 AdminTickets: Error navigating to ticket chat:', error);
                AdminUtils.showNotification('Failed to open ticket chat. Please try the floating chat instead.', 'error');
            }
        }, 500);
    }

    return {
        initialize,
        loadTicketDetails,
        updateTicketStatus,
        closeTicketDetail,
        startTicketChat,
        filterByStatus,
        filterByClient,
        getTicketsTable: () => ticketsTable,
        getCurrentTicketData: () => currentTicketData
    };
})();
