"use strict";

window.AdminInquiries = (() => {

    let currentInquiryData = null;
    let inquiriesTable = null;

    function initialize() {
        const inquiryTable = $('#inquiriesDataTable');
        if (inquiryTable.length && !$.fn.DataTable.isDataTable('#inquiriesDataTable')) {
            inquiriesTable = inquiryTable.DataTable({
                "ajax": {
                    "url": "/api/v1/admin/inquiries",
                    "dataSrc": "items"
                },
                "columns": [
                    {
                        "data": "id",
                        "title": "ID",
                        "width": "10%",
                        "className": "text-center fw-bold",
                        "render": function (data) {
                            return `<span class="badge bg-light text-dark border">#INQ-${data}</span>`;
                        }
                    },
                    {
                        "data": "topic",
                        "title": "Topic",
                        "width": "30%",
                        "render": function (data, type, row) {
                            const topic = data || 'General Inquiry';
                            const truncated = topic.length > 40 ? topic.substring(0, 40) + '...' : topic;
                            return `<span title="${AdminUtils.escapeHtml(topic)}" class="text-primary fw-semibold">${AdminUtils.escapeHtml(truncated)}</span>`;
                        }
                    },
                    {
                        "data": "inquiredBy",
                        "title": "Inquired By",
                        "width": "25%",
                        "render": function (data) {
                            return AdminUtils.escapeHtml(data || 'Unknown');
                        }
                    },
                    {
                        "data": "outcome",
                        "title": "Outcome",
                        "width": "20%",
                        "className": "text-center",
                        "render": function (data) {
                            const status = data || 'Pending';
                            const statusClass = status === 'Completed' ? 'badge-status-completed' : 'badge-status-pending';
                            return `<span class="badge ${statusClass}"><i class="fas fa-circle me-1" style="font-size: 0.5rem"></i>${AdminUtils.escapeHtml(status)}</span>`;
                        }
                    },
                    {
                        "data": null,
                        "title": "Actions",
                        "orderable": false,
                        "width": "15%",
                        "className": "text-center",
                        "render": function () {
                            return `
                                <div class="action-buttons">
                                    <button class="btn-icon-action view-inquiry-details-btn" title="View Details">
                                        <i class="fas fa-eye"></i>
                                    </button>
                                    <button class="btn-icon-action start-inquiry-chat-btn" title="Open Chat">
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
                    "emptyTable": "No inquiries found.",
                    "search": '<i class="fas fa-search me-2"></i>',
                    "lengthMenu": 'Show _MENU_ inquiries',
                    "info": 'Showing _START_ to _END_ of _TOTAL_ inquiries',
                    "infoEmpty": 'No inquiries available',
                    "paginate": {
                        "first": '<i class="fas fa-angle-double-left"></i>',
                        "last": '<i class="fas fa-angle-double-right"></i>',
                        "next": '<i class="fas fa-angle-right"></i>',
                        "previous": '<i class="fas fa-angle-left"></i>'
                    }
                },
                "createdRow": function (row, data, dataIndex) {
                    $(row).addClass('clickable-inquiry-row');
                    $(row).attr('data-inquiry-id', data.id);
                    $(row).attr('title', 'Click to open dedicated chat');
                }
            });

            setupInquiryTableEvents();
        }
    }

    function setupInquiryTableEvents() {
        if (!inquiriesTable) return;

        const inquiryTable = $('#inquiriesDataTable');

        inquiryTable.on('click', '.view-inquiry-details-btn', function (e) {
            e.stopPropagation();
            const rowData = inquiriesTable.row($(this).parents('tr')).data();
            if (rowData) {
                loadInquiryDetails(rowData.id);
            }
        });

        inquiryTable.on('click', '.start-inquiry-chat-btn', function (e) {
            e.stopPropagation();
            const data = inquiriesTable.row($(this).parents('tr')).data();
            if (data && window.AdminChat) {
                window.AdminChat.openEnhancedFloatingChatBox(data, 'inq');
            }
        });

        inquiryTable.on('click', 'tbody tr', function (e) {
            if ($(e.target).closest('.action-buttons').length === 0) {
                const rowData = inquiriesTable.row(this).data();
                if (rowData) {
                    navigateToInquiryChat(rowData);
                }
            }
        });
    }

    async function loadInquiryDetails(inquiryId) {
        try {
            console.log(`❓ AdminInquiries: Loading inquiry details for ID: ${inquiryId}`);

            const response = await fetch(`/api/v1/inquiries/${inquiryId}`);
            if (!response.ok) {
                throw new Error(`Failed to load inquiry details: ${response.statusText}`);
            }

            const inquiry = await response.json();
            currentInquiryData = inquiry;

            console.log('❓ AdminInquiries: Loaded inquiry data:', inquiry);

            $('#detail-inquiry-id').text(`#INQ-${inquiry.id}`);
            $('#detail-inquiry-topic').text(inquiry.topic || 'General Inquiry');
            $('#detail-inquiry-outcome').val(inquiry.outcome || 'Pending');
            $('#detail-inquiry-inquired-by').val(inquiry.inquiredBy || 'Unknown');

            if (inquiry.date) {
                $('#detail-inquiry-date').val(new Date(inquiry.date).toLocaleString());
            } else {
                $('#detail-inquiry-date').val('N/A');
            }

            $('#detail-inquiry-description').val(inquiry.description || inquiry.instruction || 'No description provided.');

            $('#inquiry-detail-placeholder').hide();
            $('#inquiry-detail-content').show();

            $('.inquiry-properties').scrollTop(0);

            console.log('❓ AdminInquiries: Inquiry details loaded successfully');

        } catch (error) {
            console.error('❓ AdminInquiries: Error loading inquiry details:', error);
            AdminUtils.showNotification('Failed to load inquiry details. Please try again.', 'error');
        }
    }

    async function updateInquiryStatus() {
        if (!currentInquiryData) {
            console.error('❓ AdminInquiries: No current inquiry data for status update');
            AdminUtils.showNotification('No inquiry selected for status update.', 'error');
            return;
        }

        const newOutcome = $('#detail-inquiry-outcome').val();
        const isCompleted = newOutcome === 'Completed';

        console.log(`❓ AdminInquiries: Updating inquiry ${currentInquiryData.id} to outcome: ${newOutcome}`);

        try {
            const updateBtn = $('#btn-update-inquiry');
            updateBtn.prop('disabled', true).html('<i class="fas fa-spinner fa-spin"></i> Updating...');

            const response = await fetch(`/api/v1/admin/inquiries/${currentInquiryData.id}/status`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ status: newOutcome, expectedVersion: currentInquiryData.version })
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({ message: 'Unknown error' }));
                throw new Error(errorData.message || `HTTP ${response.status}: ${response.statusText}`);
            }

            const result = await response.json();
            console.log('❓ AdminInquiries: Update response:', result);

            if (result && result.id) {
                currentInquiryData.version = result.version;
                currentInquiryData.outcome = newOutcome;

                if (inquiriesTable) {
                    inquiriesTable.ajax.reload(null, false);
                }

                if ($('#dashboard-page').hasClass('active') && window.AdminDashboard) {
                    const currentClientId = window.AdminCore?.getCurrentClientId();
                    window.AdminDashboard.loadEnhancedDashboardData(currentClientId);
                }

                AdminUtils.showNotification(`Inquiry outcome updated to ${newOutcome} successfully!`, 'success');

            } else {
                throw new Error(result.message || 'Update failed');
            }

        } catch (error) {
            console.error('❓ AdminInquiries: Error updating inquiry status:', error);
            AdminUtils.showNotification(`Failed to update inquiry status: ${error.message}`, 'error');
        } finally {
            const updateBtn = $('#btn-update-inquiry');
            updateBtn.prop('disabled', false);
            updateBtn.html('<i class="fas fa-save"></i> Update Outcome');
        }
    }

    function closeInquiryDetail() {
        currentInquiryData = null;
        $('#inquiry-detail-content').hide();
        $('#inquiry-detail-placeholder').show();

        console.log('❓ AdminInquiries: Inquiry detail panel closed');
    }

    function startInquiryChat() {
        if (currentInquiryData && window.AdminChat) {
            console.log('❓ AdminInquiries: Starting inquiry chat for:', currentInquiryData);
            window.AdminChat.openEnhancedFloatingChatBox(currentInquiryData, 'inq');
        } else {
            console.warn('❓ AdminInquiries: No current inquiry data or AdminChat module not available');
            AdminUtils.showNotification('Unable to start chat. Please try again.', 'error');
        }
    }

    async function navigateToInquiryChat(inquiryData) {
        console.log('❓ AdminInquiries: Navigating to inquiry chat for:', inquiryData);

        if (inquiryData.clientName) {
            const clientOption = $('.client-switcher option').filter(function () {
                return $(this).text() === inquiryData.clientName;
            });

            if (clientOption.length > 0) {
                const clientId = clientOption.val();
                if (window.AdminCore) {
                    window.AdminCore.setCurrentClientId(clientId);
                }
                $('.client-switcher').val(clientId);
                console.log(`❓ AdminInquiries: Set client to: ${inquiryData.clientName} (ID: ${clientId})`);
            }
        }

        if (window.AdminNavigation) {
            window.AdminNavigation.navigateToChatsPage();
        }

        setTimeout(async () => {
            try {
                let conversationItem = $(`.admin-conversation-item[data-id="${inquiryData.id}"]`);

                if (conversationItem.length === 0 && window.AdminChat) {
                    console.log('❓ AdminInquiries: Conversation not found, refreshing sidebar...');
                    const currentClientId = window.AdminCore?.getCurrentClientId();
                    await window.AdminChat.refreshAdminConversations(currentClientId);

                    setTimeout(() => {
                        conversationItem = $(`.admin-conversation-item[data-id="${inquiryData.id}"]`);

                        if (conversationItem.length > 0) {
                            conversationItem.click();
                            AdminUtils.showNotification(`Opened chat for Inquiry #${inquiryData.id}`, 'success');
                        } else {
                            console.warn(`❓ AdminInquiries: Chat conversation for Inquiry #${inquiryData.id} not found`);
                            AdminUtils.showNotification(`Chat conversation for Inquiry #${inquiryData.id} not found. Please use the floating chat instead.`, 'warning');
                        }
                    }, 1000);
                } else if (conversationItem.length > 0) {
                    conversationItem.click();
                    AdminUtils.showNotification(`Opened chat for Inquiry #${inquiryData.id}`, 'success');
                } else {
                    console.warn('❓ AdminInquiries: AdminChat module not available');
                    AdminUtils.showNotification('Chat functionality not available. Please refresh the page.', 'error');
                }
            } catch (error) {
                console.error('❓ AdminInquiries: Error navigating to inquiry chat:', error);
                AdminUtils.showNotification('Failed to open inquiry chat. Please try the floating chat instead.', 'error');
            }
        }, 500);
    }

    function filterByStatus(status) {
        if (inquiriesTable) {
            console.log(`❓ AdminInquiries: Filtering by status: ${status}`);
            const searchTerm = status ? `^${status}$` : '';
            inquiriesTable.column(3).search(searchTerm, true, false).draw();
        }
    }

    function filterByClient(clientName) {
        if (inquiriesTable) {
            console.log(`❓ AdminInquiries: Filtering by client: ${clientName}`);
            const searchTerm = clientName ? `^${clientName}$` : '';
            inquiriesTable.column(2).search(searchTerm, true, false).draw();
        }
    }

    function refreshTable() {
        if (inquiriesTable) {
            console.log('❓ AdminInquiries: Refreshing inquiries table');
            inquiriesTable.ajax.reload(null, false);
        }
    }

    function getSelectedInquiries() {
        const selectedRows = inquiriesTable.rows('.selected').data();
        return Array.from(selectedRows);
    }

    async function bulkUpdateStatus(newStatus) {
        const selectedInquiries = getSelectedInquiries();

        if (selectedInquiries.length === 0) {
            AdminUtils.showNotification('Please select inquiries to update.', 'warning');
            return;
        }

        const confirmMessage = `Are you sure you want to update ${selectedInquiries.length} inquiries to "${newStatus}"?`;
        if (!confirm(confirmMessage)) return;

        try {
            const promises = selectedInquiries.map(inquiry =>
                fetch(`/api/v1/admin/inquiries/${inquiry.id}/status`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ status: newStatus, expectedVersion: inquiry.version })
                })
            );

            const results = await Promise.allSettled(promises);
            const successful = results.filter(r => r.status === 'fulfilled').length;
            const failed = results.length - successful;

            if (successful > 0) {
                refreshTable();
                AdminUtils.showNotification(`Successfully updated ${successful} inquiries.`, 'success');
            }

            if (failed > 0) {
                AdminUtils.showNotification(`Failed to update ${failed} inquiries.`, 'error');
            }

        } catch (error) {
            console.error('❓ AdminInquiries: Error in bulk update:', error);
            AdminUtils.showNotification('Error performing bulk update.', 'error');
        }
    }

    function searchInquiries(searchTerm) {
        if (inquiriesTable) {
            console.log(`❓ AdminInquiries: Searching for: ${searchTerm}`);
            inquiriesTable.search(searchTerm).draw();
        }
    }

    function exportInquiriesToCSV() {
        try {
            const data = inquiriesTable.rows().data().toArray();
            const csvContent = convertToCSV(data);
            downloadCSV(csvContent, 'inquiries_export.csv');
            AdminUtils.showNotification('Inquiries exported successfully!', 'success');
        } catch (error) {
            console.error('❓ AdminInquiries: Error exporting CSV:', error);
            AdminUtils.showNotification('Failed to export inquiries.', 'error');
        }
    }

    function convertToCSV(data) {
        const headers = ['ID', 'Topic', 'Inquired By', 'Date', 'Outcome', 'Description'];
        const csvRows = [headers.join(',')];

        data.forEach(inquiry => {
            const row = [
                inquiry.id,
                `"${(inquiry.topic || '').replace(/"/g, '""')}"`,
                `"${(inquiry.inquiredBy || '').replace(/"/g, '""')}"`,
                inquiry.date ? new Date(inquiry.date).toLocaleDateString() : '',
                inquiry.outcome || 'Pending',
                `"${(inquiry.description || inquiry.instruction || '').replace(/"/g, '""')}"`
            ];
            csvRows.push(row.join(','));
        });

        return csvRows.join('\n');
    }

    function downloadCSV(csvContent, filename) {
        const blob = new Blob([csvContent], { type: 'text/csv' });
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
    }

    function getInquiryStatistics() {
        if (!inquiriesTable) return null;

        const data = inquiriesTable.rows().data().toArray();
        const stats = {
            total: data.length,
            pending: data.filter(i => i.outcome === 'Pending').length,
            completed: data.filter(i => i.outcome === 'Completed').length,
            topics: {}
        };

        // Count by topic
        data.forEach(inquiry => {
            const topic = inquiry.topic || 'Unknown';
            stats.topics[topic] = (stats.topics[topic] || 0) + 1;
        });

        return stats;
    }

    function setupEventHandlers() {
        $('#status-filter-inquiries').on('change', function () {
            const status = $(this).val();
            filterByStatus(status);
        });

        $('#inquiry-search-input').on('keyup', AdminUtils.debounce(function () {
            const searchTerm = $(this).val();
            searchInquiries(searchTerm);
        }, 300));

        $('#export-inquiries-btn').on('click', exportInquiriesToCSV);

        $('#bulk-complete-btn').on('click', () => bulkUpdateStatus('Completed'));
        $('#bulk-pending-btn').on('click', () => bulkUpdateStatus('Pending'));

        $('#inquiriesDataTable tbody').on('click', 'tr', function () {
            if (!$(this).hasClass('selected')) {
                $(this).addClass('selected');
            } else {
                $(this).removeClass('selected');
            }
        });

        console.log('❓ AdminInquiries: Event handlers setup complete');
    }

    function setupTableFeatures() {
        if (!inquiriesTable) return;

        const searchBox = $('#inquiriesDataTable_wrapper .dataTables_filter input');
        searchBox.attr('placeholder', 'Search inquiries...').addClass('form-control-sm');

        const tableHeader = $('#inquiriesDataTable_wrapper .dataTables_length');
        if (tableHeader.length && !$('#refresh-inquiries-btn').length) {
            tableHeader.after(`
                <div class="d-inline-block ms-3">
                    <button id="refresh-inquiries-btn" class="btn btn-outline-secondary btn-sm" title="Refresh">
                        <i class="fas fa-sync-alt"></i>
                    </button>
                </div>
            `);

            $('#refresh-inquiries-btn').on('click', function () {
                $(this).find('i').addClass('fa-spin');
                refreshTable();
                setTimeout(() => {
                    $(this).find('i').removeClass('fa-spin');
                }, 1000);
            });
        }
    }

    return {
        initialize,
        loadInquiryDetails,
        updateInquiryStatus,
        closeInquiryDetail,
        startInquiryChat,
        navigateToInquiryChat,
        getInquiriesTable: () => inquiriesTable,
        getCurrentInquiryData: () => currentInquiryData,
        filterByStatus,
        filterByClient,
        refreshTable,
        searchInquiries,
        exportInquiriesToCSV,
        getInquiryStatistics,
        getSelectedInquiries,
        bulkUpdateStatus,
        setupEventHandlers,
        setupTableFeatures
    };
})();
