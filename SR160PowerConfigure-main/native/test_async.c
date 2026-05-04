/*
 * Test with async transfers: submit IN before OUT so response isn't lost
 */
#include <libusb-1.0/libusb.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#define SR160_VID 0x2047
#define SR160_PID 0x0301

static volatile int g_rx_done = 0;
static int g_rx_len = 0;
static unsigned char g_rx_buf[64];

static void rx_callback(struct libusb_transfer *transfer)
{
    printf("RX callback: status=%d, actual_length=%d\n",
           transfer->status, transfer->actual_length);
    if (transfer->status == LIBUSB_TRANSFER_COMPLETED && transfer->actual_length > 0) {
        g_rx_len = transfer->actual_length;
        memcpy(g_rx_buf, transfer->buffer, g_rx_len);
        printf("RX data [%d]: ", g_rx_len);
        for (int i = 0; i < g_rx_len; i++) printf("%02X ", g_rx_buf[i]);
        printf("\n");
    } else if (transfer->status == LIBUSB_TRANSFER_TIMED_OUT) {
        printf("RX: TIMEOUT\n");
    } else {
        printf("RX: status=%d (%s)\n", transfer->status,
               transfer->status == LIBUSB_TRANSFER_CANCELLED ? "cancelled" :
               transfer->status == LIBUSB_TRANSFER_NO_DEVICE ? "no_device" :
               transfer->status == LIBUSB_TRANSFER_STALL ? "stall" : "other");
    }
    g_rx_done = 1;
}

int main(void)
{
    libusb_context *ctx;
    libusb_init(&ctx);

    libusb_device_handle *dev = libusb_open_device_with_vid_pid(ctx, SR160_VID, SR160_PID);
    if (!dev) { printf("Not found\n"); return 1; }

    libusb_set_auto_detach_kernel_driver(dev, 1);
    libusb_detach_kernel_driver(dev, 0);
    int rc = libusb_claim_interface(dev, 0);
    printf("Claim: %s\n", libusb_error_name(rc));
    if (rc < 0) return 1;

    libusb_clear_halt(dev, 0x01);
    libusb_clear_halt(dev, 0x82);

    /* GetPower: BB 00 B7 00 00 B7 7E */
    unsigned char cmd[64] = {0};
    cmd[0]=0xBB; cmd[1]=0x00; cmd[2]=0xB7; cmd[3]=0x00;
    cmd[4]=0x00; cmd[5]=0xB7; cmd[6]=0x7E;

    /* 1. Submit async IN transfer FIRST */
    struct libusb_transfer *rx_xfer = libusb_alloc_transfer(0);
    unsigned char rx_buf[64] = {0};
    libusb_fill_interrupt_transfer(rx_xfer, dev, 0x82,
        rx_buf, 64, rx_callback, NULL, 5000);
    g_rx_done = 0;
    rc = libusb_submit_transfer(rx_xfer);
    printf("Submit IN: %s\n", libusb_error_name(rc));

    /* 2. Small delay to ensure IN transfer is queued */
    usleep(10000);

    /* 3. Send command via sync OUT transfer */
    int transferred = 0;
    printf("Sending GetPower...\n");
    rc = libusb_interrupt_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
    printf("Write: %s (transferred=%d)\n", libusb_error_name(rc), transferred);

    /* 4. Handle events until IN transfer completes */
    printf("Waiting for response...\n");
    struct timeval tv = {0, 500000}; /* 500ms per iteration */
    for (int i = 0; i < 12 && !g_rx_done; i++) {
        libusb_handle_events_timeout(ctx, &tv);
    }

    if (!g_rx_done) {
        printf("Still waiting... cancelling\n");
        libusb_cancel_transfer(rx_xfer);
        tv.tv_sec = 1; tv.tv_usec = 0;
        libusb_handle_events_timeout(ctx, &tv);
    }

    libusb_free_transfer(rx_xfer);

    /* Try again with multiple IN transfers pending */
    printf("\n=== Attempt 2: multiple pending IN transfers ===\n");
    struct libusb_transfer *rx_xfers[4];
    unsigned char rx_bufs[4][64];

    for (int i = 0; i < 4; i++) {
        rx_xfers[i] = libusb_alloc_transfer(0);
        memset(rx_bufs[i], 0, 64);
        libusb_fill_interrupt_transfer(rx_xfers[i], dev, 0x82,
            rx_bufs[i], 64, rx_callback, NULL, 5000);
        g_rx_done = 0;
        rc = libusb_submit_transfer(rx_xfers[i]);
        printf("Submit IN #%d: %s\n", i, libusb_error_name(rc));
    }
    usleep(50000);

    transferred = 0;
    rc = libusb_interrupt_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
    printf("Write: %s (transferred=%d)\n", libusb_error_name(rc), transferred);

    printf("Waiting...\n");
    for (int i = 0; i < 12; i++) {
        tv.tv_sec = 0; tv.tv_usec = 500000;
        libusb_handle_events_timeout(ctx, &tv);
        if (g_rx_len > 0) break;
    }

    for (int i = 0; i < 4; i++) {
        libusb_cancel_transfer(rx_xfers[i]);
    }
    tv.tv_sec = 1; tv.tv_usec = 0;
    libusb_handle_events_timeout(ctx, &tv);
    for (int i = 0; i < 4; i++) {
        libusb_free_transfer(rx_xfers[i]);
    }

    libusb_release_interface(dev, 0);
    libusb_close(dev);
    libusb_exit(ctx);
    printf("Done.\n");
    return 0;
}
