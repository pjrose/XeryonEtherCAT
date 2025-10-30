#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <stdio.h>
#include "soem_shim.h"
#include "soem/soem.h"

#if defined(_WIN32)
#define SOEMSHIM_EXPORT __declspec(dllexport)
#else
#define SOEMSHIM_EXPORT __attribute__((visibility("default")))
#endif

// add for C++ consumers
#ifdef __cplusplus
extern "C" {
#endif

// Helper static assert that works in C and C++
#if defined(__cplusplus)
#define STATIC_ASSERT(cond, msg) static_assert(cond, msg)
#else
#define STATIC_ASSERT(cond, msg) _Static_assert(cond, msg)
#endif


// Expected raw IO byte sizes according to slaveinfo mapping, confirmed from 'EtherCAT commands - Xeryon.pdf'
#define IO_RX_BYTES 20  // "Output size: 160bits" -> 20 bytes
#define IO_TX_BYTES 8   // "Input size: 64bits"  -> 8 bytes

typedef void (*soem_log_callback_t)(soem_log_level_t level, const char* message);
static soem_log_callback_t log_cb = NULL;
SOEMSHIM_EXPORT void soem_set_log_callback(soem_log_callback_t cb)
 {
    log_cb = cb;
}
static void log_message(soem_log_level_t lvl, const char* fmt, ...)
{
    if (!log_cb) return;

    va_list ap;
    va_start(ap, fmt);

    // First, determine the required size (excluding null terminator)
    int needed = vsnprintf(NULL, 0, fmt, ap);
    va_end(ap);

    if (needed < 0) {
        // Fallback: formatting error, use a static message
        log_cb(lvl, "log_message: formatting error");
        return;
    }

    // Allocate buffer for the formatted message (+1 for null terminator)
    size_t bufsize = (size_t)needed + 1;
    char* buf = (bufsize <= 512) ? (char[512]){0} : (char*)malloc(bufsize);

    if (!buf) {
        log_cb(lvl, "log_message: out of memory");
        return;
    }

    va_start(ap, fmt);
    vsnprintf(buf, bufsize, fmt, ap);
    va_end(ap);

    log_cb(lvl, buf);

    if (bufsize > 512) {
        free(buf);
    }
}


SOEMSHIM_EXPORT int soem_drain_error_list(soem_handle_t* h, char* buf, int buf_sz)
{
    if (!h || !buf || buf_sz <= 0) {
        LOGE("Invalid arguments: h=%p, buf=%p, buf_sz=%d\n", h, buf, buf_sz);
        return 0;
    }

    char* err = ecx_elist2string(&h->context);
    if (!err) {
        LOGE("No error string available.\n");
        buf[0] = '\0';
        return 1;
    }

    size_t n = strnlen(err, (size_t)buf_sz - 1);
    memcpy(buf, err, n);
    buf[n] = '\0';
    if(n > 0) {
        LOGE("Drained error string: %s\n", buf);
	}
    return 1;
}

SOEMSHIM_EXPORT void soem_shutdown(soem_handle_t *handle)
{
    if (!handle) return;
    handle->context.slavelist[0].state = EC_STATE_INIT;
    ecx_writestate(&handle->context, 0);
    ecx_close(&handle->context);
    free(handle->IOmap);   
    free(handle);
}

SOEMSHIM_EXPORT int soem_get_slave_count(soem_handle_t *handle)
{
    if (handle == NULL)
    {
        return 0;
    }

    return handle->context.slavecount;
}

SOEMSHIM_EXPORT int soem_get_process_sizes(soem_handle_t *handle, int *outputs, int *inputs)
{
    if (handle == NULL || outputs == NULL || inputs == NULL)
    {
        return 0;
    }

    *outputs = handle->output_length;
    *inputs = handle->input_length;
    return 1;
}

SOEMSHIM_EXPORT int soem_scan_slaves(soem_handle_t *handle, soem_slave_info_t *buffer, int max_count)
{
    if (handle == NULL || buffer == NULL || max_count <= 0)
    {
        return 0;
    }

    int count = handle->context.slavecount;
    if (count > max_count)
    {
        count = max_count;
    }

    for (int i = 0; i < count; ++i)
    {
        ec_slavet *slave = &handle->context.slavelist[i + 1];
        buffer[i].position = i + 1;
        buffer[i].vendor_id = slave->eep_man;
        buffer[i].product_code = slave->eep_id;
        buffer[i].revision = slave->eep_rev;
        strncpy(buffer[i].name, slave->name, EC_MAXNAME);
        buffer[i].name[EC_MAXNAME] = '\0';
    }

    return count;
}

/* Export raw expected sizes so managed clients can validate at runtime */
SOEMSHIM_EXPORT int soem_expected_rx_bytes(void) { return IO_RX_BYTES; }
SOEMSHIM_EXPORT int soem_expected_tx_bytes(void) { return IO_TX_BYTES; }

/* Read and unpack TX PDO (input) into high-level DriveTxPDO.
   Returns 1 on success, 0 on failure. */
SOEMSHIM_EXPORT int soem_read_txpdo(soem_handle_t* handle, int slave_index, DriveTxPDO* out)
{
    if (!handle || !out || slave_index <= 0 || slave_index > handle->context.slavecount) return 0;

    ec_slavet* slave = &handle->context.slavelist[slave_index];
    if (!slave || !slave->inputs) return 0;

    // Explicit bounds check to prevent buffer overflow
    if ((int)slave->Ibytes < IO_TX_BYTES) {
        LOGE("soem_read_txpdo: slave %d Ibytes too small (%d < %d)", slave_index, (int)slave->Ibytes, IO_TX_BYTES);
        return 0;
    }

    uint8_t* buf = (uint8_t*)slave->inputs;

    // little-endian; copy to avoid aliasing issues
    int32_t pos;
    memcpy(&pos, &buf[0], sizeof(pos));
    out->ActualPosition = pos;

    // byte 4: bits 0..7
    uint8_t b4 = buf[4];
    out->AmplifiersEnabled = (b4 >> 0) & 0x1;
    out->EndStop = (b4 >> 1) & 0x1;
    out->ThermalProtection1 = (b4 >> 2) & 0x1;
    out->ThermalProtection2 = (b4 >> 3) & 0x1;
    out->ForceZero = (b4 >> 4) & 0x1;
    out->MotorOn = (b4 >> 5) & 0x1;
    out->ClosedLoop = (b4 >> 6) & 0x1;
    out->EncoderIndex = (b4 >> 7) & 0x1;

    // byte 5
    uint8_t b5 = buf[5];
    out->EncoderValid = (b5 >> 0) & 0x1;
    out->SearchingIndex = (b5 >> 1) & 0x1;
    out->PositionReached = (b5 >> 2) & 0x1;
    out->ErrorCompensation = (b5 >> 3) & 0x1;
    out->EncoderError = (b5 >> 4) & 0x1;
    out->Scanning = (b5 >> 5) & 0x1;
    out->LeftEndStop = (b5 >> 6) & 0x1;
    out->RightEndStop = (b5 >> 7) & 0x1;

    // byte 6
    uint8_t b6 = buf[6];
    out->ErrorLimit = (b6 >> 0) & 0x1;
    out->SearchingOptimalFrequency = (b6 >> 1) & 0x1;
    out->SafetyTimeout = (b6 >> 2) & 0x1;
    out->ExecuteAck = (b6 >> 3) & 0x1;
    out->EmergencyStop = (b6 >> 4) & 0x1;
    out->PositionFail = (b6 >> 5) & 0x1;

    // slot at byte 7
    out->Slot = buf[7];

    return 1;
}

/* Pack and write RX PDO (output) from high-level DriveRxPDO.
   Returns 1 on success, 0 on failure. */
SOEMSHIM_EXPORT int soem_write_rxpdo(soem_handle_t* handle, int slave_index, const DriveRxPDO* in)
{
    if (!handle || !in || slave_index <= 0 || slave_index > handle->context.slavecount) return 0;

    ec_slavet* slave = &handle->context.slavelist[slave_index];
    if (!slave || !slave->outputs) return 0;

    // Explicit bounds check to prevent buffer overflow
    if ((int)slave->Obytes < IO_RX_BYTES) {
        LOGE("soem_write_rxpdo: slave %d Obytes too small (%d < %d)", slave_index, (int)slave->Obytes, IO_RX_BYTES);
        return 0;
    }

    uint8_t* buf = (uint8_t*)slave->outputs;

    // Command: 4 bytes at offset 0
    memcpy(&buf[0], in->Command, 4);

    // Parameter: 4 bytes at offset 4
    memcpy(&buf[4], &in->Parameter, 4);

    // Velocity: 4 bytes at offset 8
    memcpy(&buf[8], &in->Velocity, 4);

    // Acceleration: 2 bytes at offset 12
    memcpy(&buf[12], &in->Acceleration, 2);

    // Deceleration: 2 bytes at offset 14
    memcpy(&buf[14], &in->Deceleration, 2);

    // Execute: 1 byte at offset 16
    buf[16] = in->Execute;

    // The remaining bytes up to IO_RX_BYTES are left unchanged or can be zeroed if desired.
    return 1;
}

SOEMSHIM_EXPORT int soem_exchange_process_data(
    soem_handle_t* h,
    const uint8_t* outputs, int outputs_len,
    uint8_t* inputs, int inputs_len,
    int timeout_us)
{
    if (!h) return SOEM_ERR_BAD_ARGS;

    ec_groupt* g = &h->context.grouplist[0];

    // Clamp timeout to non-negative
    if (timeout_us < 0) timeout_us = 0;

    // Prepare outputs: zero-tail every cycle, then copy up to Obytes
    if (g->Obytes) {
        if (outputs && outputs_len > 0) {
            memset(g->outputs, 0, g->Obytes);                 // only when caller passes outputs
            int copy = outputs_len < (int)g->Obytes ? outputs_len : (int)g->Obytes;
            if (copy > 0) memcpy(g->outputs, outputs, (size_t)copy);
        }
        // else: leave IOmap outputs as-is (e.g., set by soem_write_rxpdo)
    }

    int wkc = ecx_send_processdata(&h->context);
    int expected = (int)(g->outputsWKC * 2 + g->inputsWKC);

    if (wkc < 0) {
        LOGE("ecx_send_processdata failed rc=%d (expected WKC=%d)", wkc, expected);
        return SOEM_ERR_SEND_FAIL;
    }

    wkc = ecx_receive_processdata(&h->context, timeout_us);
    h->last_wkc = wkc;  
    h->last_expected_wkc = expected;

    if (wkc < 0) {
        LOGE("ecx_receive_processdata failed rc=%d (expected WKC=%d, timeout_us=%d)", wkc, expected, timeout_us);
        return SOEM_ERR_RECV_FAIL;
    }

    // Copy inputs up to Ibytes
    if (inputs && inputs_len > 0 && g->Ibytes) {
        int copy = inputs_len < (int)g->Ibytes ? inputs_len : (int)g->Ibytes;
        if (copy > 0) memcpy(inputs, g->inputs, (size_t)copy);
    }

    // If expected is zero (misconfigured group), don't false-trigger; just log once.
    if (expected <= 0) {
        LOGW("Expected WKC is %d (check mapping). Returning wkc=%d", expected, wkc);
        return wkc; // best-effort
    }

    if (wkc < expected) {
        LOGE("WKC low: got=%d expected=%d (Obytes=%u Ibytes=%u, oWKC=%u iWKC=%u)",
            wkc, expected, (unsigned)g->Obytes, (unsigned)g->Ibytes,
            (unsigned)g->outputsWKC, (unsigned)g->inputsWKC);
        return SOEM_ERR_WKC_LOW;
    }

    return wkc;  // OK
}

SOEMSHIM_EXPORT soem_handle_t* soem_initialize(const char* ifname)
{
    soem_handle_t* handle = (soem_handle_t*)calloc(1, sizeof(soem_handle_t));
    if (!handle) return NULL;

    handle->last_wkc = -1;
    handle->last_expected_wkc = 0;

    // returns greater than 0 if successful
    if (!ecx_init(&handle->context, ifname))
    {
        LOGE("ecx_init failed for interface '%s'", ifname ? ifname : "(null)");
        free(handle);
        return NULL;
    }

    // returns number of slaves found, if <=0 no slaves found, shutdown.
    int slave_count = ecx_config_init(&handle->context);
    if (slave_count <= 0)
    {
        LOGE("ecx_config_init failed: no slaves found or error (rc=%d)", slave_count);
        ecx_close(&handle->context);
        free(handle);
        return NULL;
    }

    // allocate a comfortably sized IOmap (you can tune this; 64 KiB is conservative)
    size_t iomap_size = 64 * 1024;
    handle->IOmap = (uint8*)malloc(iomap_size);

    // if allocation failed, cleanup and exit
    if (!handle->IOmap)
    {
        LOGE("IOmap allocation failed (size=%zu)", iomap_size);
        ecx_close(&handle->context);
        free(handle);
        return NULL;
    }
    memset(handle->IOmap, 0, iomap_size); // Zero IOmap after alloc.

    // returns IO map size, if <=0 no IO map configured, shutdown.
    int actual_size = ecx_config_map_group(&handle->context, handle->IOmap, 0);
    if (actual_size <= 0)
    {
        LOGE("ecx_config_map_group failed: rc=%d", actual_size);
        ecx_close(&handle->context);
        free(handle->IOmap);
        free(handle);
        return NULL;
    }
    if ((size_t)actual_size > iomap_size)
    {
        LOGE("ecx_config_map_group: actual IOmap size (%d) exceeds allocated size (%zu). Aborting to prevent memory corruption.", actual_size, iomap_size);
        ecx_close(&handle->context);
        free(handle->IOmap);
        free(handle);
        return NULL;
    }

    ecx_configdc(&handle->context);

    int count = soem_get_slave_count(handle);

    // Stage outputs (NOP, Execute=0) into IOmap
    for (int i = 1; i <= count; ++i) {
        DriveRxPDO rx = { 0 };
        memcpy(rx.Command, "NOP", 3);           // dont copy the '\0' unless your field expects it
        // Execute stays 0
        if (!soem_write_rxpdo(handle, i, &rx)) {
            LOGE("write_rxpdo failed for slave %d", i);
        }
    }

    // One actual bus cycle (send+recv). No outputs buffer -> uses staged IOmap.
    int rc = soem_exchange_process_data(handle, NULL, 0, NULL, 0, 2000);
    if (rc < 0) LOGE("probe exchange failed rc=%d", rc);


    // Now read inputs that were received
    for (int i = 1; i <= count; ++i) {
        DriveTxPDO tx = { 0 };
        if (soem_read_txpdo(handle, i, &tx)) {
            LOGI("Slave %d ActualPosition=%d", i, tx.ActualPosition);
        }
    }

    ecx_statecheck(&handle->context, 0, EC_STATE_SAFE_OP, EC_TIMEOUTSTATE * 4);

    handle->context.slavelist[0].state = EC_STATE_OPERATIONAL;
    ecx_writestate(&handle->context, 0);
    ecx_statecheck(&handle->context, 0, EC_STATE_OPERATIONAL, EC_TIMEOUTSTATE);

    ec_groupt *group = &handle->context.grouplist[0];
    handle->output_length = (int)group->Obytes;
    handle->input_length  = (int)group->Ibytes;

    return handle;
}

SOEMSHIM_EXPORT int soem_try_recover(soem_handle_t* h, int timeout_ms)
{
    if (!h) return 0;
    ecx_readstate(&h->context);

    for (int i = 1; i <= h->context.slavecount; ++i) {
        ec_slavet* s = &h->context.slavelist[i];
        if (s->state & EC_STATE_ERROR) {
            s->state = (uint16)(EC_STATE_SAFE_OP | EC_STATE_ACK);
            ecx_writestate(&h->context, i);
        }
    }

    h->context.slavelist[0].state = EC_STATE_OPERATIONAL;
    ecx_writestate(&h->context, 0);

    for (int k = 0; k < 3; ++k) {
        ecx_send_processdata(&h->context);
        ecx_receive_processdata(&h->context, 1000);
    }

    // group-level check
    int rc = ecx_statecheck(&h->context, 0, EC_STATE_OPERATIONAL, timeout_ms * 1000);
    if (rc != EC_STATE_OPERATIONAL)
    {
        log_message(SOEM_LOG_WARN, "Group failed to reach OP in %d ms", timeout_ms);
        return 0;
    }
    log_message(SOEM_LOG_INFO, "Recovery OK");

    // per-slave verify
    int ok = 1;
    for (int i = 1; i <= h->context.slavecount; ++i) {
        if (h->context.slavelist[i].state != EC_STATE_OPERATIONAL) {
            ok = 0; break;
        }
    }
    return ok;
}

SOEMSHIM_EXPORT int soem_get_health(soem_handle_t* h, soem_health_t* out)
{
    if (!h || !out) return 0;
    memset(out, 0, sizeof(*out));
    ec_groupt* g = &h->context.grouplist[0];
    out->slaves_found = h->context.slavecount;
    out->group_expected_wkc = (int)((g->outputsWKC * 2) + g->inputsWKC);
    out->last_wkc = h->last_wkc; // set in exchange
    out->bytes_out = (int)g->Obytes;
    out->bytes_in = (int)g->Ibytes;

    ecx_readstate(&h->context);
    int op = 0;
    for (int i = 1; i <= h->context.slavecount; ++i)
        if (h->context.slavelist[i].state == EC_STATE_OPERATIONAL) ++op;
    out->slaves_op = op;

    // best-effort AL status from first slave (optional)
    if (h->context.slavecount >= 1)
        out->al_status_code = h->context.slavelist[1].ALstatuscode;

    return 1;
}

SOEMSHIM_EXPORT int soem_get_network_adapters()
{
    
    ec_adaptert* adapter = NULL;
    ec_adaptert* head = NULL;

    LOGI("\nAvailable adapters:\n");
    head = adapter = ec_find_adapters();
    while (adapter != NULL)
    {
        LOGI("    - %s  (%s)\n", adapter->name, adapter->desc);
        adapter = adapter->next;
    }
    ec_free_adapters(head);
    return 1;
}

#ifdef __cplusplus
}
#endif