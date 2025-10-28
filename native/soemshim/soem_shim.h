#pragma once
#include "soem/soem.h"
#include <stdint.h>

#if defined(_WIN32)
#define SOEMSHIM_EXPORT __declspec(dllexport)
#else
#define SOEMSHIM_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif


typedef enum { SOEM_LOG_INFO = 0, SOEM_LOG_WARN = 1, SOEM_LOG_ERR = 2 } soem_log_level_t;

#ifndef SOEM_ERR_CODES
#define SOEM_ERR_CODES
#define SOEM_ERR_BAD_ARGS   (-13)
#define SOEM_ERR_SEND_FAIL  (-11)
#define SOEM_ERR_RECV_FAIL  (-12)
#define SOEM_ERR_WKC_LOW    (-10)
#endif

    // log_message(level, fmt, ...)
#ifndef LOGI
#define LOGI(...) log_message(SOEM_LOG_INFO, __VA_ARGS__)
#define LOGW(...) log_message(SOEM_LOG_WARN, __VA_ARGS__)
#define LOGE(...) log_message(SOEM_LOG_ERR,  __VA_ARGS__)
#endif

typedef struct soem_handle soem_handle_t;

// NOTE: soem_handle_t and all soemshim functions operating on a given handle are NOT thread-safe.
// If multiple threads need to access the same soem_handle_t, all access must be externally serialized
// (e.g., using a mutex or critical section). Concurrent use from multiple threads may result in
// undefined behavior, data corruption, or crashes.
typedef struct soem_handle
{
    ecx_contextt context;
    uint8* IOmap;            // <-- pointer, not fixed array
    int output_length;
    int input_length;
    int last_wkc;
    int last_expected_wkc;
} soem_handle_t;

//determined from running slaveinfo from the SOEM package on the NIC connected to the backplane
    #if defined(_MSC_VER)
    #pragma pack(push, 1)
    typedef struct { 
        char Command[32];         // OCTET_STR(32), i.e. 4 letter ASCII command, STOP, DPOS, INDX, etc..
        int32_t Parameter;        // INTEGER32
        uint32_t Velocity;        // UNSIGNED32
        uint16_t Acceleration;    // UNSIGNED16
        uint16_t Deceleration;    // UNSIGNED16
        uint8_t Execute;          // UNSIGNED8
    } DriveRxPDO;
    #pragma pack(pop)
    #pragma pack(push, 1)
    typedef struct {
        int32_t ActualPosition;
        uint8_t AmplifiersEnabled;
        uint8_t EndStop;
        uint8_t ThermalProtection1;
        uint8_t ThermalProtection2;
        uint8_t ForceZero;
        uint8_t MotorOn;
        uint8_t ClosedLoop;
        uint8_t EncoderIndex;
        uint8_t EncoderValid;
        uint8_t SearchingIndex;
        uint8_t PositionReached;
        uint8_t ErrorCompensation;
        uint8_t EncoderError;
        uint8_t Scanning;
        uint8_t LeftEndStop;
        uint8_t RightEndStop;
        uint8_t ErrorLimit;
        uint8_t SearchingOptimalFrequency;
        uint8_t SafetyTimeout;
        uint8_t ExecuteAck;
        uint8_t EmergencyStop;
        uint8_t PositionFail;
        uint8_t Slot;
    } DriveTxPDO;
    #pragma pack(pop)
#else
    typedef struct __attribute__((packed)) { 
        char Command[32];
        int32_t Parameter;
        uint32_t Velocity;
        uint16_t Acceleration;
        uint16_t Deceleration;
        uint8_t Execute;
    } DriveRxPDO;
    typedef struct __attribute__((packed)) {
        int32_t ActualPosition;
        uint8_t AmplifiersEnabled;
        uint8_t EndStop;
        uint8_t ThermalProtection1;
        uint8_t ThermalProtection2;
        uint8_t ForceZero;
        uint8_t MotorOn;
        uint8_t ClosedLoop;
        uint8_t EncoderIndex;
        uint8_t EncoderValid;
        uint8_t SearchingIndex;
        uint8_t PositionReached;
        uint8_t ErrorCompensation;
        uint8_t EncoderError;
        uint8_t Scanning;
        uint8_t LeftEndStop;
        uint8_t RightEndStop;
        uint8_t ErrorLimit;
        uint8_t SearchingOptimalFrequency;
        uint8_t SafetyTimeout;
        uint8_t ExecuteAck;
        uint8_t EmergencyStop;
        uint8_t PositionFail;
        uint8_t Slot;
    } DriveTxPDO;
#endif

typedef struct soem_slave_info
{
    int position;
    uint32_t vendor_id;
    uint32_t product_code;
    uint32_t revision;
    char name[EC_MAXNAME + 1];
} soem_slave_info_t;


typedef struct soem_health {
    int slaves_found;
    int group_expected_wkc;
    int last_wkc;
    int bytes_out;
    int bytes_in;
    int slaves_op;            // count currently OP
    uint32_t al_status_code;  // 0 if unknown
} soem_health_t;


SOEMSHIM_EXPORT soem_handle_t* soem_initialize(const char* ifname);
SOEMSHIM_EXPORT void soem_shutdown(soem_handle_t* handle);
SOEMSHIM_EXPORT int  soem_get_slave_count(soem_handle_t* handle);
SOEMSHIM_EXPORT int  soem_get_process_sizes(soem_handle_t* h, int* outputs, int* inputs);
SOEMSHIM_EXPORT int  soem_scan_slaves(soem_handle_t* h, soem_slave_info_t* buf, int max_count);
SOEMSHIM_EXPORT int  soem_expected_rx_bytes(void);
SOEMSHIM_EXPORT int  soem_expected_tx_bytes(void);
SOEMSHIM_EXPORT int  soem_read_txpdo(soem_handle_t* h, int slave_index, DriveTxPDO* out);
SOEMSHIM_EXPORT int  soem_write_rxpdo(soem_handle_t* h, int slave_index, const DriveRxPDO* in);
SOEMSHIM_EXPORT int  soem_exchange_process_data(soem_handle_t* h, const uint8_t* outputs, int outputs_len, uint8_t* inputs, int inputs_len, int timeout_us);
SOEMSHIM_EXPORT int  soem_try_recover(soem_handle_t* h, int timeout_ms);

/* Return a pointer to a null-terminated error string.
   - returns "invalid handle" if h is NULL
   - returns empty string ("") if there are no errors
   - otherwise returns a pointer to an internal buffer containing the readable error text
   The returned pointer is valid until the next call and is safe for P/Invoke string marshaling. */
SOEMSHIM_EXPORT int soem_drain_error_list(soem_handle_t* h, char* buf, int buf_sz);
SOEMSHIM_EXPORT int  soem_get_health(soem_handle_t* h, soem_health_t* out);


#ifdef __cplusplus
}
#endif
