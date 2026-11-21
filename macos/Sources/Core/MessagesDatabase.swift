import Foundation
import SQLite3

/// Read-only access to the Messages database. Keeps one connection open between polls and caches
/// which optional columns the schema has; reopens when the database file is replaced or a query fails.
/// Not thread-safe: use it from one serial queue.
final class MessagesDatabase {
    struct ReadError: Error {
        let code: Int32
    }

    private struct Schema {
        let hasItemType: Bool
        let hasAssociatedType: Bool
        let hasChatJoin: Bool
        let hasChatDisplayName: Bool
        let hasChatRoomName: Bool
        let hasIsRead: Bool
        let hasDateRead: Bool
    }

    let path: String
    private var database: OpaquePointer?
    private var openedFileIdentity: FileIdentity?
    private var schema: Schema?

    init(path: String) {
        self.path = path
    }

    deinit {
        close()
    }

    func close() {
        if let database { sqlite3_close(database) }
        database = nil
        openedFileIdentity = nil
        schema = nil
    }

    func maximumRowID() throws -> Int64 {
        let db = try connection()
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT COALESCE(MAX(ROWID),0) FROM message", -1, &statement, nil) == SQLITE_OK,
              let stmt = statement else { throw failure(db) }
        defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_ROW else { throw failure(db) }
        return sqlite3_column_int64(stmt, 0)
    }

    /// Incoming rows after `rowID`, oldest first.
    func incomingMessages(after rowID: Int64, includeAssociatedMessages: Bool, limit: Int = 100) throws -> [PendingMessage] {
        let db = try connection()
        let schema = try currentSchema(db)
        let reactionExpression = schema.hasAssociatedType ? "COALESCE(m.associated_message_type, 0)" : "0"
        let displayNameExpression = schema.hasChatDisplayName ? "COALESCE(c.display_name, '')" : "''"
        let roomNameExpression = schema.hasChatRoomName ? "COALESCE(c.room_name, '')" : "''"
        let joins = schema.hasChatJoin ? " LEFT JOIN chat_message_join AS cmj ON cmj.message_id = m.ROWID LEFT JOIN chat AS c ON c.ROWID = cmj.chat_id" : ""
        let itemTypeFilter = schema.hasItemType ? " AND COALESCE(m.item_type, 0) = 0" : ""
        let reactionFilter = schema.hasAssociatedType ? " AND (?2 = 1 OR COALESCE(m.associated_message_type, 0) = 0)" : ""
        let sql = "SELECT m.ROWID, COALESCE(h.id, ''), COALESCE(m.text, ''), COALESCE(m.date, 0), COALESCE(m.guid, ''), \(reactionExpression), \(displayNameExpression), \(roomNameExpression) FROM message AS m LEFT JOIN handle AS h ON h.ROWID = m.handle_id\(joins) WHERE m.is_from_me = 0\(itemTypeFilter)\(reactionFilter) AND m.ROWID > ?1 ORDER BY m.ROWID ASC LIMIT \(max(1, limit))"

        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let stmt = statement else { throw failure(db) }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_int64(stmt, 1, rowID)
        if schema.hasAssociatedType { sqlite3_bind_int(stmt, 2, includeAssociatedMessages ? 1 : 0) }

        var messages: [PendingMessage] = []
        var result = sqlite3_step(stmt)
        while result == SQLITE_ROW {
            let address = Self.text(stmt, 1)
            let rawPreview = Self.text(stmt, 2)
            let rawDate = sqlite3_column_double(stmt, 3)
            let guid = Self.text(stmt, 4)
            let isAssociatedMessage = sqlite3_column_int(stmt, 5) != 0
            let displayName = Self.text(stmt, 6)
            let roomName = Self.text(stmt, 7)
            let preview = rawPreview
                .components(separatedBy: .newlines)
                .joined(separator: " ")
                .components(separatedBy: .whitespaces)
                .filter { !$0.isEmpty }
                .joined(separator: " ")
            let decodedDate = TimestampDecoder.decode(rawDate)
            messages.append(PendingMessage(
                rowID: sqlite3_column_int64(stmt, 0),
                guid: guid.trimmingCharacters(in: .whitespacesAndNewlines),
                senderAddress: address,
                preview: String((preview.isEmpty && isAssociatedMessage ? "Associated message event received" : preview).prefix(180)),
                messageDate: decodedDate?.date,
                groupName: displayName.isEmpty ? roomName : displayName,
                isAssociatedMessage: isAssociatedMessage,
                timestampUnit: decodedDate?.unit
            ))
            result = sqlite3_step(stmt)
        }
        guard result == SQLITE_DONE else { throw failure(db) }
        return messages
    }

    /// Read state of one message and the user's latest activity in its conversation, looking at that
    /// conversation's 50 most recent other messages. Read-only, like every query here.
    func attention(forRowID rowID: Int64) throws -> MessageAttention {
        let db = try connection()
        let schema = try currentSchema(db)

        var isRead = false
        if schema.hasIsRead {
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(db, "SELECT COALESCE(is_read, 0) FROM message WHERE ROWID = ?1", -1, &statement, nil) == SQLITE_OK,
                  let stmt = statement else { throw failure(db) }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int64(stmt, 1, rowID)
            let result = sqlite3_step(stmt)
            if result == SQLITE_ROW {
                isRead = sqlite3_column_int(stmt, 0) != 0
            } else if result != SQLITE_DONE {
                throw failure(db)
            }
        }

        var lastActivity: Date?
        if schema.hasChatJoin {
            let readExpression = schema.hasDateRead ? "COALESCE(m.date_read, 0)" : "0"
            let sql = "SELECT m.is_from_me, COALESCE(m.date, 0), \(readExpression) FROM chat_message_join AS cmj JOIN message AS m ON m.ROWID = cmj.message_id WHERE cmj.chat_id = (SELECT chat_id FROM chat_message_join WHERE message_id = ?1 LIMIT 1) AND cmj.message_id <> ?1 ORDER BY cmj.message_id DESC LIMIT 50"
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let stmt = statement else { throw failure(db) }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int64(stmt, 1, rowID)
            var result = sqlite3_step(stmt)
            while result == SQLITE_ROW {
                let raw = sqlite3_column_int(stmt, 0) != 0 ? sqlite3_column_double(stmt, 1) : sqlite3_column_double(stmt, 2)
                if let date = TimestampDecoder.decode(raw)?.date, lastActivity.map({ date > $0 }) ?? true {
                    lastActivity = date
                }
                result = sqlite3_step(stmt)
            }
            guard result == SQLITE_DONE else { throw failure(db) }
        }
        return MessageAttention(isRead: isRead, lastActivity: lastActivity)
    }

    static func describe(_ error: Error) -> String {
        let code = (error as? ReadError)?.code ?? SQLITE_ERROR
        if code == SQLITE_CANTOPEN || code == SQLITE_AUTH || code == SQLITE_PERM {
            return "Can't read Messages. Give Bubblecast Full Disk Access in System Settings → Privacy & Security."
        }
        return "Messages database is temporarily unavailable (SQLite error \(code))."
    }

    // MARK: - Connection

    private func connection() throws -> OpaquePointer {
        let identity = FileIdentity(path: path)
        if let database, identity != nil, identity == openedFileIdentity {
            return database
        }
        close()
        var opened: OpaquePointer?
        let result = sqlite3_open_v2(path, &opened, SQLITE_OPEN_READONLY, nil)
        guard result == SQLITE_OK, let db = opened else {
            let code = opened.map { sqlite3_errcode($0) } ?? result
            if let opened { sqlite3_close(opened) }
            throw ReadError(code: code)
        }
        sqlite3_busy_timeout(db, 500)
        database = db
        openedFileIdentity = identity
        return db
    }

    /// Closes the connection so the next call reopens it, and returns the error to throw.
    private func failure(_ db: OpaquePointer) -> ReadError {
        let error = ReadError(code: sqlite3_errcode(db))
        close()
        return error
    }

    private func currentSchema(_ db: OpaquePointer) throws -> Schema {
        if let schema { return schema }
        let hasChatJoin = tableExists("chat_message_join", in: db) && tableExists("chat", in: db) &&
            columnExists("chat_message_join", "message_id", in: db) && columnExists("chat_message_join", "chat_id", in: db)
        let loaded = Schema(
            hasItemType: columnExists("message", "item_type", in: db),
            hasAssociatedType: columnExists("message", "associated_message_type", in: db),
            hasChatJoin: hasChatJoin,
            hasChatDisplayName: hasChatJoin && columnExists("chat", "display_name", in: db),
            hasChatRoomName: hasChatJoin && columnExists("chat", "room_name", in: db),
            hasIsRead: columnExists("message", "is_read", in: db),
            hasDateRead: columnExists("message", "date_read", in: db)
        )
        schema = loaded
        return loaded
    }

    private func tableExists(_ name: String, in db: OpaquePointer) -> Bool {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?1 LIMIT 1", -1, &statement, nil) == SQLITE_OK,
              let stmt = statement else { return false }
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, name, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self))
        return sqlite3_step(stmt) == SQLITE_ROW
    }

    private func columnExists(_ table: String, _ column: String, in db: OpaquePointer) -> Bool {
        guard ["message", "chat", "chat_message_join"].contains(table) else { return false }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, "PRAGMA table_info(\(table))", -1, &statement, nil) == SQLITE_OK,
              let stmt = statement else { return false }
        defer { sqlite3_finalize(stmt) }
        while sqlite3_step(stmt) == SQLITE_ROW {
            if let value = sqlite3_column_text(stmt, 1), String(cString: value).caseInsensitiveCompare(column) == .orderedSame {
                return true
            }
        }
        return false
    }

    private static func text(_ stmt: OpaquePointer, _ column: Int32) -> String {
        sqlite3_column_text(stmt, column).map { String(cString: $0) } ?? ""
    }
}

/// Device and inode of a file, used to notice when the database file is replaced.
struct FileIdentity: Equatable {
    let device: UInt64
    let inode: UInt64

    init?(path: String) {
        var info = stat()
        guard stat(path, &info) == 0 else { return nil }
        device = UInt64(bitPattern: Int64(info.st_dev))
        inode = UInt64(info.st_ino)
    }
}
