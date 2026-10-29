package com.notilink.app.net

import org.json.JSONObject
import java.nio.ByteBuffer
import java.util.UUID

/**
 * Wire format per docs/PROTOCOL.md — keep this file and that document in sync.
 */
object Protocol {
    const val TCP_PORT = 45212
    const val UDP_DISCOVERY_PORT = 45211
    const val HEADER_LENGTH = 5

    const val FRAME_TYPE_JSON: Byte = 0x01
    const val FRAME_TYPE_BINARY: Byte = 0x02

    fun buildEnvelope(type: String, payload: JSONObject): JSONObject {
        return JSONObject().apply {
            put("v", 1)
            put("type", type)
            put("id", UUID.randomUUID().toString())
            put("ts", System.currentTimeMillis())
            put("payload", payload)
        }
    }

    /** [4-byte big-endian length][1-byte type=0x01][UTF-8 JSON payload] */
    fun frameJson(envelope: JSONObject): ByteArray {
        val payloadBytes = envelope.toString().toByteArray(Charsets.UTF_8)
        return ByteBuffer.allocate(4 + 1 + payloadBytes.size)
            .putInt(payloadBytes.size)
            .put(FRAME_TYPE_JSON)
            .put(payloadBytes)
            .array()
    }

    /** [4-byte big-endian length][1-byte type=0x02][raw binary payload] */
    fun frameBinary(bytes: ByteArray): ByteArray {
        return ByteBuffer.allocate(4 + 1 + bytes.size)
            .putInt(bytes.size)
            .put(FRAME_TYPE_BINARY)
            .put(bytes)
            .array()
    }

    /** Decodes a [HEADER_LENGTH]-byte frame header into (payload length, frame type). */
    fun decodeHeader(header: ByteArray): Pair<Int, Byte> {
        val buffer = ByteBuffer.wrap(header)
        val length = buffer.int
        val type = buffer.get()
        return length to type
    }
}
