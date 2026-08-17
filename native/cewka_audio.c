/*
    cewka_audio - cienka nakladka na miniaudio dla warstwy zarzadzanej.

    Powod istnienia: struktury konfiguracyjne miniaudio (ma_device_config, ma_decoder)
    sa duze i zmieniaja uklad miedzy wersjami. Odwzorowywanie ich w C# byloby krucha
    robota, ktora psulaby sie po kazdej aktualizacji biblioteki. Zamiast tego cala praca
    ze strukturami dzieje sie tutaj, a do C# wystawiony jest zestaw plaskich funkcji
    operujacych wylacznie na typach prostych i wskaznikach nieprzezroczystych.

    Zakres celowo waski. Warstwa zarzadzana odpowiada za caly tor przetwarzania sygnalu;
    stad potrzebne sa tylko: urzadzenie wyjsciowe, dekoder, resampler i wyliczenie urzadzen.

    Budowanie: patrz build-windows.cmd oraz build-linux.sh w tym katalogu.
*/

/* Wylaczenie czesci, ktorych projekt nie uzywa - mniejszy plik wynikowy i krotsza kompilacja. */
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio/miniaudio.h"

#include <string.h>
#include <stdlib.h>

#if defined(_WIN32)
    #define CEWKA_API __declspec(dllexport)
#else
    #define CEWKA_API __attribute__((visibility("default")))
#endif

/* ============================================================================
   Kontekst i wyliczanie urzadzen
   ============================================================================ */

static ma_context      g_context;
static int             g_contextReady = 0;
static ma_device_info* g_playbackInfos = NULL;
static ma_uint32       g_playbackCount = 0;

static ma_result cewka__ensure_context(void)
{
    if (g_contextReady) {
        return MA_SUCCESS;
    }

    ma_result result = ma_context_init(NULL, 0, NULL, &g_context);
    if (result != MA_SUCCESS) {
        return result;
    }

    g_contextReady = 1;
    return MA_SUCCESS;
}

/* Odswieza liste urzadzen odtwarzania i zwraca ich liczbe przez outCount. */
CEWKA_API int cewka_devices_refresh(int* outCount)
{
    ma_result result = cewka__ensure_context();
    if (result != MA_SUCCESS) {
        return (int)result;
    }

    g_playbackInfos = NULL;
    g_playbackCount = 0;

    result = ma_context_get_devices(&g_context, &g_playbackInfos, &g_playbackCount, NULL, NULL);
    if (result != MA_SUCCESS) {
        return (int)result;
    }

    if (outCount != NULL) {
        *outCount = (int)g_playbackCount;
    }

    return (int)MA_SUCCESS;
}

/* Kopiuje nazwe urzadzenia jako ciag zakonczony zerem. Zwraca liczbe zapisanych bajtow bez zera. */
CEWKA_API int cewka_devices_name(int index, char* buffer, int bufferSize)
{
    if (index < 0 || (ma_uint32)index >= g_playbackCount || buffer == NULL || bufferSize <= 0) {
        return -1;
    }

    const char* name = g_playbackInfos[index].name;
    int length = (int)strlen(name);
    if (length >= bufferSize) {
        length = bufferSize - 1;
    }

    memcpy(buffer, name, (size_t)length);
    buffer[length] = '\0';
    return length;
}

/* 1 gdy urzadzenie jest domyslne w systemie. */
CEWKA_API int cewka_devices_is_default(int index)
{
    if (index < 0 || (ma_uint32)index >= g_playbackCount) {
        return 0;
    }

    return g_playbackInfos[index].isDefault ? 1 : 0;
}

/* ============================================================================
   Urzadzenie wyjsciowe
   ============================================================================ */

typedef void (*cewka_data_cb)(void* pUser, float* pFrames, ma_uint32 frameCount);

typedef struct
{
    ma_device     device;
    cewka_data_cb callback;
    void*         user;
    ma_uint32     channels;
} cewka_device;

static void cewka__device_data(ma_device* pDevice, void* pOutput, const void* pInput, ma_uint32 frameCount)
{
    cewka_device* self = (cewka_device*)pDevice->pUserData;
    (void)pInput;

    if (self != NULL && self->callback != NULL) {
        self->callback(self->user, (float*)pOutput, frameCount);
    } else {
        /* Cisza jest jedyna bezpieczna odpowiedzia, gdy nie ma kto wypelnic bufora. */
        memset(pOutput, 0, (size_t)frameCount * self->channels * sizeof(float));
    }
}

/*
    Tworzy urzadzenie odtwarzania w formacie float32.

    deviceIndex        -1 oznacza urzadzenie domyslne, w przeciwnym razie indeks z cewka_devices_refresh
    sampleRate         0 oznacza czestotliwosc natywna urzadzenia
    periodSizeInFrames 0 oznacza wartosc dobrana przez miniaudio
*/
CEWKA_API int cewka_device_create(int           deviceIndex,
                                  ma_uint32     sampleRate,
                                  ma_uint32     channels,
                                  ma_uint32     periodSizeInFrames,
                                  cewka_data_cb callback,
                                  void*         user,
                                  void**        outHandle)
{
    if (outHandle == NULL) {
        return (int)MA_INVALID_ARGS;
    }
    *outHandle = NULL;

    ma_result result = cewka__ensure_context();
    if (result != MA_SUCCESS) {
        return (int)result;
    }

    cewka_device* self = (cewka_device*)ma_malloc(sizeof(cewka_device), NULL);
    if (self == NULL) {
        return (int)MA_OUT_OF_MEMORY;
    }
    memset(self, 0, sizeof(*self));

    self->callback = callback;
    self->user     = user;
    self->channels = channels;

    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.format   = ma_format_f32;
    config.playback.channels = channels;
    config.sampleRate        = sampleRate;
    config.dataCallback      = cewka__device_data;
    config.pUserData         = self;

    if (periodSizeInFrames > 0) {
        config.periodSizeInFrames = periodSizeInFrames;
    }

    if (deviceIndex >= 0 && (ma_uint32)deviceIndex < g_playbackCount) {
        config.playback.pDeviceID = &g_playbackInfos[deviceIndex].id;
    }

    result = ma_device_init(&g_context, &config, &self->device);
    if (result != MA_SUCCESS) {
        ma_free(self, NULL);
        return (int)result;
    }

    /* Urzadzenie moglo wybrac inna liczbe kanalow niz zadana. */
    self->channels = self->device.playback.channels;

    *outHandle = self;
    return (int)MA_SUCCESS;
}

CEWKA_API int cewka_device_start(void* handle)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;
    return (int)ma_device_start(&((cewka_device*)handle)->device);
}

CEWKA_API int cewka_device_stop(void* handle)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;
    return (int)ma_device_stop(&((cewka_device*)handle)->device);
}

CEWKA_API void cewka_device_destroy(void* handle)
{
    if (handle == NULL) return;

    cewka_device* self = (cewka_device*)handle;
    ma_device_uninit(&self->device);
    ma_free(self, NULL);
}

CEWKA_API ma_uint32 cewka_device_sample_rate(void* handle)
{
    if (handle == NULL) return 0;
    return ((cewka_device*)handle)->device.sampleRate;
}

CEWKA_API ma_uint32 cewka_device_channels(void* handle)
{
    if (handle == NULL) return 0;
    return ((cewka_device*)handle)->device.playback.channels;
}

/*
    Rozmiar okresu, ktory urzadzenie faktycznie przyjelo. Zadany rozmiar bufora
    jest tylko podpowiedzia - sterownik moze go zmienic albo zignorowac, wiec
    ustawienie opoznienia bez tego odczytu byloby deklaracja, a nie faktem.
*/
CEWKA_API ma_uint32 cewka_device_period(void* handle)
{
    if (handle == NULL) return 0;
    return ((cewka_device*)handle)->device.playback.internalPeriodSizeInFrames;
}

/* Nazwa faktycznie uzywanego urzadzenia - przydatna w oknie ustawien i w diagnostyce. */
CEWKA_API int cewka_device_name(void* handle, char* buffer, int bufferSize)
{
    if (handle == NULL || buffer == NULL || bufferSize <= 0) {
        return -1;
    }

    cewka_device* self = (cewka_device*)handle;
    const char* name = self->device.playback.name;
    int length = (int)strlen(name);
    if (length >= bufferSize) {
        length = bufferSize - 1;
    }

    memcpy(buffer, name, (size_t)length);
    buffer[length] = '\0';
    return length;
}

/* ============================================================================
   Dekoder

   Strumien plikowy pozostaje po stronie C#, bo tylko tam sciezki z polskimi znakami
   sa obslugiwane poprawnie na obu systemach, a plik nie musi trafiac w calosci do pamieci.
   ============================================================================ */

typedef size_t (*cewka_read_cb)(void* user, void* buffer, size_t bytesToRead);
typedef int    (*cewka_seek_cb)(void* user, long long offset, int origin); /* 0 = poczatek, 1 = biezaca */

typedef struct
{
    ma_decoder    decoder;
    cewka_read_cb read;
    cewka_seek_cb seek;
    void*         user;
} cewka_decoder;

static ma_result cewka__decoder_read(ma_decoder* pDecoder, void* pBufferOut, size_t bytesToRead, size_t* pBytesRead)
{
    cewka_decoder* self = (cewka_decoder*)pDecoder->pUserData;
    size_t read = self->read(self->user, pBufferOut, bytesToRead);

    if (pBytesRead != NULL) {
        *pBytesRead = read;
    }

    return read == 0 ? MA_AT_END : MA_SUCCESS;
}

static ma_result cewka__decoder_seek(ma_decoder* pDecoder, ma_int64 byteOffset, ma_seek_origin origin)
{
    cewka_decoder* self = (cewka_decoder*)pDecoder->pUserData;
    int ok = self->seek(self->user, (long long)byteOffset, origin == ma_seek_origin_start ? 0 : 1);
    return ok ? MA_SUCCESS : MA_ERROR;
}

/*
    Otwiera dekoder. outRate i outChannels rowne zeru oznaczaja format zrodlowy;
    podanie wartosci wlacza wbudowana konwersje miniaudio.

    lpfOrder dotyczy tej wbudowanej konwersji - MP3, FLAC i WAV resampluje
    dekoder miniaudio, a nie cewka_resampler_*. Znaczenie wartosci jak tam.
*/
CEWKA_API int cewka_decoder_open(cewka_read_cb read,
                                 cewka_seek_cb seek,
                                 void*         user,
                                 ma_uint32     outRate,
                                 ma_uint32     outChannels,
                                 ma_uint32     lpfOrder,
                                 void**        outHandle,
                                 ma_uint64*    outLengthFrames,
                                 ma_uint32*    outSourceRate,
                                 ma_uint32*    outSourceChannels)
{
    if (outHandle == NULL || read == NULL || seek == NULL) {
        return (int)MA_INVALID_ARGS;
    }
    *outHandle = NULL;

    cewka_decoder* self = (cewka_decoder*)ma_malloc(sizeof(cewka_decoder), NULL);
    if (self == NULL) {
        return (int)MA_OUT_OF_MEMORY;
    }
    memset(self, 0, sizeof(*self));

    self->read = read;
    self->seek = seek;
    self->user = user;

    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, outChannels, outRate);

    if (lpfOrder > MA_MAX_FILTER_ORDER) {
        lpfOrder = MA_MAX_FILTER_ORDER;
    }
    config.resampling.linear.lpfOrder = lpfOrder;

    ma_result result = ma_decoder_init(cewka__decoder_read, cewka__decoder_seek, self, &config, &self->decoder);
    if (result != MA_SUCCESS) {
        ma_free(self, NULL);
        return (int)result;
    }

    if (outLengthFrames != NULL) {
        ma_uint64 length = 0;
        /* Dlugosc podawana jest w ramkach wyjsciowych, a wiec juz po konwersji.
           Dla strumieni bez znanej dlugosci zwracane jest zero - to nie jest blad. */
        if (ma_decoder_get_length_in_pcm_frames(&self->decoder, &length) != MA_SUCCESS) {
            length = 0;
        }
        *outLengthFrames = length;
    }

    if (outSourceRate != NULL || outSourceChannels != NULL) {
        ma_format format;
        ma_uint32 channels = 0;
        ma_uint32 rate = 0;

        /*
            Pytanie idzie do zaplecza dekodera, a nie do samego dekodera.
            ma_decoder_get_data_format opisuje to, co dekoder wydaje na zewnatrz, czyli format
            juz przekonwertowany - dla pliku 44,1 kHz odtwarzanego przez urzadzenie 48 kHz
            zwrocilby 48 kHz. Format pliku zna dopiero zaplecze (dr_mp3, dr_flac, dr_wav).
        */
        ma_result info = MA_ERROR;
        if (self->decoder.pBackend != NULL) {
            info = ma_data_source_get_data_format(
                self->decoder.pBackend, &format, &channels, &rate, NULL, 0);
        }
        if (info != MA_SUCCESS) {
            info = ma_decoder_get_data_format(&self->decoder, &format, &channels, &rate, NULL, 0);
        }

        if (info == MA_SUCCESS) {
            if (outSourceRate != NULL)     *outSourceRate = rate;
            if (outSourceChannels != NULL) *outSourceChannels = channels;
        }
    }

    *outHandle = self;
    return (int)MA_SUCCESS;
}

/* Zwraca liczbe faktycznie odczytanych ramek; zero oznacza koniec strumienia. */
CEWKA_API ma_uint64 cewka_decoder_read(void* handle, float* output, ma_uint64 frameCount)
{
    if (handle == NULL || output == NULL) {
        return 0;
    }

    ma_uint64 read = 0;
    ma_decoder_read_pcm_frames(&((cewka_decoder*)handle)->decoder, output, frameCount, &read);
    return read;
}

CEWKA_API int cewka_decoder_seek(void* handle, ma_uint64 frameIndex)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;
    return (int)ma_decoder_seek_to_pcm_frame(&((cewka_decoder*)handle)->decoder, frameIndex);
}

CEWKA_API void cewka_decoder_close(void* handle)
{
    if (handle == NULL) return;

    cewka_decoder* self = (cewka_decoder*)handle;
    ma_decoder_uninit(&self->decoder);
    ma_free(self, NULL);
}

/* ============================================================================
   Resampler

   Uzywany przez dekodery zarzadzane (Vorbis, Opus, Media Foundation, GStreamer),
   ktore zwracaja probki w czestotliwosci zrodlowej i wymagaja dopasowania
   do czestotliwosci urzadzenia.

   lpfOrder to rzad filtru dolnoprzepustowego: 0 wylacza filtrowanie, 4 jest
   wartoscia domyslna miniaudio, 8 to maksimum (MA_MAX_FILTER_ORDER). Filtr
   dziala przed zmniejszeniem czestotliwosci i po jej zwiekszeniu, wiec jego
   rzad decyduje o tym, ile aliasingu przechodzi na wyjscie.
   ============================================================================ */

CEWKA_API int cewka_resampler_create(ma_uint32 channels,
                                     ma_uint32 rateIn,
                                     ma_uint32 rateOut,
                                     ma_uint32 lpfOrder,
                                     void**    outHandle)
{
    if (outHandle == NULL) {
        return (int)MA_INVALID_ARGS;
    }
    *outHandle = NULL;

    if (lpfOrder > MA_MAX_FILTER_ORDER) {
        lpfOrder = MA_MAX_FILTER_ORDER;
    }

    ma_resampler* self = (ma_resampler*)ma_malloc(sizeof(ma_resampler), NULL);
    if (self == NULL) {
        return (int)MA_OUT_OF_MEMORY;
    }
    memset(self, 0, sizeof(*self));

    ma_resampler_config config = ma_resampler_config_init(
        ma_format_f32, channels, rateIn, rateOut, ma_resample_algorithm_linear);

    config.linear.lpfOrder = lpfOrder;

    ma_result result = ma_resampler_init(&config, NULL, self);
    if (result != MA_SUCCESS) {
        ma_free(self, NULL);
        return (int)result;
    }

    *outHandle = self;
    return (int)MA_SUCCESS;
}

/* Wartosci wskazywane przez frameCountIn i frameCountOut sa nadpisywane liczba ramek faktycznie uzytych. */
CEWKA_API int cewka_resampler_process(void*        handle,
                                      const float* input,
                                      ma_uint64*   frameCountIn,
                                      float*       output,
                                      ma_uint64*   frameCountOut)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;
    return (int)ma_resampler_process_pcm_frames((ma_resampler*)handle, input, frameCountIn, output, frameCountOut);
}

CEWKA_API ma_uint64 cewka_resampler_required_input(void* handle, ma_uint64 outputFrameCount)
{
    if (handle == NULL) return 0;

    ma_uint64 required = 0;
    ma_resampler_get_required_input_frame_count((ma_resampler*)handle, outputFrameCount, &required);
    return required;
}

CEWKA_API void cewka_resampler_destroy(void* handle)
{
    if (handle == NULL) return;

    ma_resampler_uninit((ma_resampler*)handle, NULL);
    ma_free(handle, NULL);
}

/* ============================================================================
   Diagnostyka
   ============================================================================ */

CEWKA_API const char* cewka_version(void)
{
    return ma_version_string();
}

CEWKA_API const char* cewka_result_description(int result)
{
    return ma_result_description((ma_result)result);
}
