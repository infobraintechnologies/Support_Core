"use strict";

window.AdminTickets = (() => {

    let currentTicketData = null;
    let ticketsTable = null;

    function initialize() {
        const ticketTable = $('#ticketsTable');
        if (ticketTable.length && !$.fn.DataTable.isDataTable('#ticketsTable')) {
            ticketsTable = ticketTable.DataTable({
                "ajax": {
                    "url": "/v1/api/instructions/tickets/all",
                    "dataSrc": "data"
                },
                "columns": [
                    {
                        "data": "id",
                        "title": "ID",
                        "width": "10%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#${data}</span>`;
                        }
                    },
                    {
                        "data": "clientName",
                        "title": "Client",
                        "width": "15%"
                    },
                    {
                        "data": "subject",
                        "title": "Subject",
                        "width": "25%"
                    },
                    {
                        "data": "createdBy",
                        "title": "Created By",
                        "width": "15%"
                    },
                    {
                        "data": "status",
                        "title": "Status",
                        "width": "12%",
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
                        "title": "Actions",
                        "orderable": false,
                        "width": "11%",
                        "className": "text-center",
                        "render": function () {
                            return `
                                <div class="action-buttons">
                                    <button class="btn-icon-action view-ticket-details-btn" title="View Details">
                                        <i class="fas fa-eye"></i>
                                    </button>
                                    <button class="btn-icon-action start-chat-btn" title="Open Chat">
                                        <i class="fas fa-comments"></i>
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
                    "emptyTable": "No tickets found.",
                    "search": '<i class="fas fa-search me-2"></i>',
                },
                "createdRow": function (row, data, dataIndex) {
                    $(row).addClass('clickable-ticket-row');
                    $(row).attr('data-ticket-id', data.id);
                    $(row).attr('title', 'Click to open dedicated chat');
                }
            });

            setupTicketTableEvents();
        }
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
                    navigateToTicketChat(rowData);
                }
            }
        });
    }

    async function loadTicketDetails(ticketId) {
        try {
            const response = await fetch(`/v1/api/instructions/tickets/${ticketId}/details`);
            if (!response.ok) throw new Error('Failed to load ticket details');

            const ticket = await response.json();
            currentTicketData = ticket;

            console.log('🎫 AdminTickets: Loaded ticket data:', ticket);

            $('#detail-ticket-id').text(`#TKT-${ticket.id}`);
            $('#detail-ticket-subject').text(ticket.subject || 'General Support');
            $('#detail-ticket-status').val(ticket.status || 'Open');
            $('#detail-ticket-priority').val(ticket.priority || 'Normal');
            $('#detail-ticket-created-by').val(ticket.createdBy || 'Unknown');
            $('#detail-ticket-client').val(ticket.clientName || 'Unknown');
            $('#detail-ticket-date').val(new Date(ticket.date).toLocaleString());
            $('#detail-ticket-description').val(ticket.description || 'No description available.');
            $('#detail-ticket-resolved-by').val(ticket.resolvedBy || '');

            if (ticket.resolvedDate) {
                $('#detail-ticket-resolved-date').val(new Date(ticket.resolvedDate).toLocaleString());
            } else {
                $('#detail-ticket-resolved-date').val('');
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
            updateBtn.prop('disabled', true).html('<i class="fas fa-spinner fa-spin"></i> Updating...');

            const response = await fetch(`/v1/api/instructions/tickets/${currentTicketData.id}/status`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ isCompleted: isCompleted })
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({ message: 'Unknown error' }));
                throw new Error(errorData.message || `HTTP ${response.status}: ${response.statusText}`);
            }

            const result = await response.json();
            console.log('🎫 AdminTickets: Update response:', result);

            if (result.success) {
                const currentUser = window.AdminCore?.getCurrentUser();
                currentTicketData.status = newStatus;

                if (isCompleted) {
                    currentTicketData.resolvedDate = new Date().toISOString();
                    currentTicketData.resolvedBy = currentUser?.name || 'Admin';
                } else {
                    currentTicketData.resolvedDate = null;
                    currentTicketData.resolvedBy = null;
                }

                if (currentTicketData.resolvedDate) {
                    $('#detail-ticket-resolved-date').val(new Date(currentTicketData.resolvedDate).toLocaleString());
                    $('#detail-ticket-resolved-by').val(currentTicketData.resolvedBy);
                } else {
                    $('#detail-ticket-resolved-date').val('');
                    $('#detail-ticket-resolved-by').val('');
                }

                if (ticketsTable) {
                    ticketsTable.ajax.reload(null, false);
                }

                AdminUtils.showNotification(`Ticket status updated to ${newStatus} successfully!`, 'success');
            } else {
                throw new Error(result.message || 'Update failed');
            }

        } catch (error) {
            console.error('🎫 AdminTickets: Error updating ticket status:', error);
            AdminUtils.showNotification(`Failed to update ticket status: ${error.message}`, 'error');
        } finally {
            const updateBtn = $('#btn-update-ticket');
            updateBtn.prop('disabled', false);
            updateBtn.html('<i class="fas fa-save"></i> Update Status');
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
        getTicketsTable: () => ticketsTable,
        getCurrentTicketData: () => currentTicketData
    };
})();