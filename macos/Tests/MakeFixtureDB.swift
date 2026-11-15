import Foundation
import SQLite3

@main
struct MakeFixtureDB {
    static func main() {
        guard CommandLine.arguments.count == 2 else {
            fputs("Usage: MakeFixtureDB <new-fixture.sqlite>\n", stderr)
            exit(2)
        }
        let outputURL = URL(fileURLWithPath: CommandLine.arguments[1]).standardizedFileURL
        guard !FileManager.default.fileExists(atPath: outputURL.path) else {
            fputs("Refusing to overwrite an existing file. Choose a new temporary path.\n", stderr)
            exit(2)
        }

        var database: OpaquePointer?
        guard sqlite3_open_v2(outputURL.path, &database, SQLITE_OPEN_CREATE | SQLITE_OPEN_READWRITE, nil) == SQLITE_OK,
              let database else {
            fputs("Could not create the synthetic fixture database.\n", stderr)
            exit(1)
        }
        defer { sqlite3_close(database) }

        let schemaAndRows = """
        CREATE TABLE handle (ROWID INTEGER PRIMARY KEY, id TEXT);
        CREATE TABLE message (
            ROWID INTEGER PRIMARY KEY, guid TEXT, text TEXT, date INTEGER,
            is_from_me INTEGER, handle_id INTEGER, item_type INTEGER DEFAULT 0,
            associated_message_type INTEGER DEFAULT 0, attributedBody BLOB,
            associated_message_emoji TEXT
        );
        CREATE TABLE chat (
            ROWID INTEGER PRIMARY KEY, display_name TEXT, room_name TEXT,
            chat_identifier TEXT, style INTEGER
        );
        CREATE TABLE chat_message_join (
            chat_id INTEGER, message_id INTEGER, message_date INTEGER DEFAULT 0
        );
        INSERT INTO handle VALUES (1, 'synthetic@example.invalid');
        INSERT INTO chat VALUES (1, 'Synthetic group', 'room-fixture', 'fixture-id', 1);
        INSERT INTO message VALUES (1, 'fixture-message-1', 'Synthetic fixture message', 0, 0, 1, 0, 0, NULL, NULL);
        INSERT INTO chat_message_join VALUES (1, 1, 0);
        """
        guard sqlite3_exec(database, schemaAndRows, nil, nil, nil) == SQLITE_OK else {
            fputs("Could not populate the synthetic fixture database.\n", stderr)
            exit(1)
        }
        print("Created one synthetic Messages-schema fixture row at \(outputURL.path)")
    }
}
