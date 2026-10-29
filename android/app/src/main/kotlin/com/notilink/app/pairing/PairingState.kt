package com.notilink.app.pairing

/**
 * In-memory bridge between the listener service's socket read loop (which
 * receives pair.code asynchronously) and MainActivity (which shows it and
 * sends pair.confirm). MainActivity registers [onCodeReceived] while visible
 * and clears it in onPause to avoid holding a stale Activity reference.
 */
object PairingState {
    var pendingCode: String? = null
        private set

    var onCodeReceived: ((String) -> Unit)? = null

    /** Fired the moment a pair.token actually arrives and is saved — lets MainActivity swap
     *  straight from the pairing-code UI to the "esperando conexión" status without needing
     *  the user to background/reopen the app first. */
    var onPaired: (() -> Unit)? = null

    fun showCode(code: String) {
        pendingCode = code
        onCodeReceived?.invoke(code)
    }

    fun clear() {
        pendingCode = null
        onPaired?.invoke()
    }
}
