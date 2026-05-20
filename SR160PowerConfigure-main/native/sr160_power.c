/*
 * sr160_power — CLI helper for SR160 power configuration via libusb.
 * Requires root/admin to detach the macOS HID kernel driver.
 *
 * Usage:
 *   sr160_power get              — read current power (prints number)
 *   sr160_power set <save> <dbm> — set power (save=0 temp, save=1 permanent)
 *
 * The GUI app invokes this via osascript with admin privileges.
 */

#include <libusb-1.0/libusb.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SR160_VID  0x2047
#define SR160_PID  0x0301
#define IFACE_NUM  0
#define EP_OUT     0x01
#define EP_IN      0x82
#define TIMEOUT_MS 3000
#define FRAME_HEAD 0xBB
#define FRAME_END  0x7E

static uint8_t calc_cs(const uint8_t *buf, int len)
{
    uint8_t cs = 0;
    for (int i = 0; i < len; i++) cs += buf[i];
    return cs;
}

static int build_frame(uint8_t *out, uint8_t type, uint8_t cmd,
                       const uint8_t *data, int dlen)
{
    int idx = 0;
    out[idx++] = FRAME_HEAD;
    out[idx++] = type;
    out[idx++] = cmd;
    out[idx++] = (uint8_t)((dlen >> 8) & 0xFF);
    out[idx++] = (uint8_t)(dlen & 0xFF);
    if (data && dlen > 0) memcpy(out + idx, data, dlen);
    idx += dlen;
    out[idx] = calc_cs(out + 1, idx - 1);
    idx++;
    out[idx++] = FRAME_END;
    return idx;
}

static int transact(libusb_device_handle *dev,
                    const uint8_t *tx, int txlen,
                    uint8_t *rx, int rxmax)
{
    uint8_t buf[64] = {0};
    int copy = txlen < 64 ? txlen : 64;
    memcpy(buf, tx, copy);

    int transferred = 0;
    int rc = libusb_interrupt_transfer(dev, EP_OUT, buf, 64, &transferred, TIMEOUT_MS);
    if (rc != 0) {
        fprintf(stderr, "Write failed: %s\n", libusb_error_name(rc));
        return -1;
    }

    memset(rx, 0, rxmax);
    transferred = 0;
    rc = libusb_interrupt_transfer(dev, EP_IN, rx, rxmax, &transferred, TIMEOUT_MS);
    if (rc != 0) {
        fprintf(stderr, "Read failed: %s\n", libusb_error_name(rc));
        return -2;
    }

    return transferred;
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "Usage: sr160_power get|set [save] [power]\n");
        return 1;
    }

    libusb_context *ctx = NULL;
    libusb_device_handle *dev = NULL;

    if (libusb_init(&ctx) < 0) {
        fprintf(stderr, "ERROR: libusb_init failed\n");
        return 2;
    }

    dev = libusb_open_device_with_vid_pid(ctx, SR160_VID, SR160_PID);
    if (!dev) {
        fprintf(stderr, "ERROR: SR160 not found\n");
        libusb_exit(ctx);
        return 3;
    }

    /* Detach kernel driver */
    libusb_set_auto_detach_kernel_driver(dev, 1);
    if (libusb_kernel_driver_active(dev, IFACE_NUM) == 1) {
        libusb_detach_kernel_driver(dev, IFACE_NUM);
    }

    int rc = libusb_claim_interface(dev, IFACE_NUM);
    if (rc < 0) {
        fprintf(stderr, "ERROR: claim_interface: %s\n", libusb_error_name(rc));
        libusb_close(dev);
        libusb_exit(ctx);
        return 4;
    }

    if (strcmp(argv[1], "get") == 0) {
        uint8_t frame[64], rx[64];
        int flen = build_frame(frame, 0x00, 0xB7, NULL, 0); /* GetPower */
        int rxlen = transact(dev, frame, flen, rx, sizeof(rx));

        if (rxlen > 0) {
            /* Find BB frame */
            for (int i = 0; i < rxlen; i++) {
                if (rx[i] == FRAME_HEAD && i + 6 < rxlen) {
                    int pl = (rx[i+3] << 8) | rx[i+4];
                    if (pl >= 1) {
                        printf("OK %d\n", rx[i+5]);
                        goto done;
                    }
                }
            }
            fprintf(stderr, "ERROR: no valid response frame\n");
            printf("ERROR parse\n");
        } else {
            printf("ERROR io\n");
        }
    }
    else if (strcmp(argv[1], "set") == 0 && argc >= 4) {
        uint8_t save = (uint8_t)atoi(argv[2]);
        uint8_t power = (uint8_t)atoi(argv[3]);
        uint8_t payload[2] = { save, power };

        uint8_t frame[64], rx[64];
        int flen = build_frame(frame, 0x00, 0xB6, payload, 2); /* SetPower */
        int rxlen = transact(dev, frame, flen, rx, sizeof(rx));

        if (rxlen > 0) {
            /* Check for valid response */
            for (int i = 0; i < rxlen; i++) {
                if (rx[i] == FRAME_HEAD) {
                    printf("OK\n");
                    goto done;
                }
            }
            fprintf(stderr, "ERROR: no valid response\n");
            printf("ERROR parse\n");
        } else {
            printf("ERROR io\n");
        }
    }
    else {
        fprintf(stderr, "Usage: sr160_power get|set [save] [power]\n");
    }

done:
    libusb_release_interface(dev, IFACE_NUM);
    libusb_close(dev);
    libusb_exit(ctx);
    return 0;
}
