#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "soem/soem.h"

#if defined(_WIN32)
#define SOEMSHIM_EXPORT __declspec(dllexport)
#else
#define SOEMSHIM_EXPORT __attribute__((visibility("default")))
#endif

typedef struct soem_handle
{
    ecx_contextt context;
    uint8 IOmap[4096];
    int output_length;
    int input_length;
} soem_handle_t;

typedef struct soem_slave_info
{
    int position;
    uint32_t vendor_id;
    uint32_t product_code;
    uint32_t revision;
    char name[EC_MAXNAME + 1];
} soem_slave_info_t;

SOEMSHIM_EXPORT soem_handle_t *soem_initialize(const char *ifname)
{
    soem_handle_t *handle = (soem_handle_t *)calloc(1, sizeof(soem_handle_t));
    if (handle == NULL)
    {
        return NULL;
    }

    if (!ecx_init(&handle->context, ifname))
    {
        free(handle);
        return NULL;
    }

    if (ecx_config_init(&handle->context) <= 0)
    {
        ecx_close(&handle->context);
        free(handle);
        return NULL;
    }

    ecx_config_map_group(&handle->context, handle->IOmap, 0);
    ecx_configdc(&handle->context);

    ecx_statecheck(&handle->context, 0, EC_STATE_SAFE_OP, EC_TIMEOUTSTATE * 4);

    handle->context.slavelist[0].state = EC_STATE_OPERATIONAL;
    ecx_writestate(&handle->context, 0);
    ecx_statecheck(&handle->context, 0, EC_STATE_OPERATIONAL, EC_TIMEOUTSTATE);

    ec_groupt *group = &handle->context.grouplist[0];
    handle->output_length = (int)group->Obytes;
    handle->input_length = (int)group->Ibytes;

    return handle;
}

SOEMSHIM_EXPORT void soem_shutdown(soem_handle_t *handle)
{
    if (handle == NULL)
    {
        return;
    }

    handle->context.slavelist[0].state = EC_STATE_INIT;
    ecx_writestate(&handle->context, 0);
    ecx_close(&handle->context);
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

SOEMSHIM_EXPORT int soem_exchange_process_data(soem_handle_t *handle, const uint8_t *outputs, int outputs_length, uint8_t *inputs, int inputs_length, int timeout)
{
    if (handle == NULL)
    {
        return -1;
    }

    ec_groupt *group = &handle->context.grouplist[0];

    if (outputs != NULL && outputs_length > 0 && group->Obytes > 0)
    {
        int copy = outputs_length;
        if (copy > (int)group->Obytes)
        {
            copy = (int)group->Obytes;
        }
        memcpy(group->outputs, outputs, (size_t)copy);
    }

    int wkc = ecx_send_processdata(&handle->context);
    if (wkc < 0)
    {
        return wkc;
    }

    wkc = ecx_receive_processdata(&handle->context, timeout);

    if (inputs != NULL && inputs_length > 0 && group->Ibytes > 0)
    {
        int copy = inputs_length;
        if (copy > (int)group->Ibytes)
        {
            copy = (int)group->Ibytes;
        }
        memcpy(inputs, group->inputs, (size_t)copy);
    }

    return wkc;
}
