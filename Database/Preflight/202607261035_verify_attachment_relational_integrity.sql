  -- CBS Support read-only database preflight
  -- Purpose: verify attachment tenant, uploader, message-binding, and quota-accounting
  -- integrity before 202607261040_enforce_attachment_relational_invariants.
  -- Expected result for every query: zero rows.

  SELECT attachment.id,
        attachment.client_id AS attachment_client_id,
        access.client_id AS conversation_client_id
  FROM digital.attachments attachment
  JOIN digital.conversation_access access
    ON access.conversation_id = attachment.conversation_id
  WHERE attachment.client_id IS DISTINCT FROM access.client_id
  ORDER BY attachment.id
  LIMIT 200;

  SELECT attachment.id,
        attachment.client_id AS attachment_client_id,
        message.client_id AS message_client_id,
        attachment.conversation_id,
        message.instruction_id AS message_conversation_id
  FROM digital.attachments attachment
  JOIN digital.instructions message ON message.id = attachment.message_id
  WHERE attachment.message_id IS NOT NULL
    AND (
          attachment.client_id IS DISTINCT FROM message.client_id
          OR attachment.conversation_id IS DISTINCT FROM message.instruction_id
    )
  ORDER BY attachment.id
  LIMIT 200;

  SELECT attachment.id,
        attachment.client_id AS attachment_client_id,
        uploader.client_id AS uploader_client_id
  FROM digital.attachments attachment
  JOIN internal.support_users uploader ON uploader.id = attachment.client_user_id
  WHERE attachment.client_user_id IS NOT NULL
    AND attachment.client_id IS DISTINCT FROM uploader.client_id
  ORDER BY attachment.id
  LIMIT 200;

  SELECT audit.audit_id,
        audit.attachment_id,
        audit.client_id AS audit_client_id,
        attachment.client_id AS attachment_client_id
  FROM digital.attachment_audit audit
  JOIN digital.attachments attachment ON attachment.id = audit.attachment_id
  WHERE audit.client_id IS DISTINCT FROM attachment.client_id
  ORDER BY audit.audit_id
  LIMIT 200;

  SELECT id, state, declared_size, actual_size, reservation_bytes
  FROM digital.attachments
  WHERE state IN (
      'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
      'Scanning','Promoting','Ready','DeletePending')
    AND reservation_bytes < GREATEST(declared_size, COALESCE(actual_size, 0))
  ORDER BY id
  LIMIT 200;

  SELECT id, message_id, bound_at, expires_at
  FROM digital.attachments
  WHERE message_id IS NOT NULL
    AND (
          expires_at IS NULL
          OR expires_at < bound_at + INTERVAL '365 days'
    )
  ORDER BY id
  LIMIT 200;

  SELECT id, state, message_id, ready_at, expires_at
  FROM digital.attachments
  WHERE state = 'Ready'
    AND message_id IS NULL
    AND (
          ready_at IS NULL
          OR expires_at IS NULL
          OR expires_at < ready_at + INTERVAL '24 hours'
    )
  ORDER BY id
  LIMIT 200;
