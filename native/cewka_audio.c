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
static int             g_useNullBackend = 0;
static ma_device_info* g_playbackInfos = NULL;
static ma_uint32       g_playbackCount = 0;

static ma_result cewka__ensure_context(void)
{
    if (g_contextReady) {
        return MA_SUCCESS;
    }

    ma_result result;
    if (g_useNullBackend) {
        ma_backend backends[] = { ma_backend_null };
        result = ma_context_init(backends, 1, NULL, &g_context);
    } else {
        result = ma_context_init(NULL, 0, NULL, &g_context);
    }

    if (result != MA_SUCCESS) {
        return result;
    }

    g_contextReady = 1;
    return MA_SUCCESS;
}

/*
    Wybiera zaplecze puste: urzadzenie, ktore pobiera probki w tempie rzeczywistym
    i niczego nie odtwarza. Dla testow i narzedzi, ktore nie moga grac przez glosniki.

    Wybor obowiazuje od utworzenia kontekstu, wiec po nim zmiana jest bledem.
*/
CEWKA_API int cewka_use_null_backend(int on)
{
    int wanted = on ? 1 : 0;

    if (g_contextReady && wanted != g_useNullBackend) {
        return (int)MA_INVALID_OPERATION;
    }

    g_useNullBackend = wanted;
    return (int)MA_SUCCESS;
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

/* Zdarzenia urzadzenia przekazywane do warstwy zarzadzanej. */
#define CEWKA_DEVICE_LOST 1

typedef void (*cewka_notify_cb)(void* pUser, int event);

typedef struct
{
    ma_device       device;
    cewka_data_cb   callback;
    cewka_notify_cb notify;
    void*           user;
    ma_uint32       channels;
    volatile int    stopRequested;
} cewka_device;

static void cewka__device_data(ma_device* pDevice, void* pOutput, const void* pInput, ma_uint32 frameCount)
{
    cewka_device* self = (cewka_device*)pDevice->pUserData;
    (void)pInput;

    if (self != NULL && self->callback != NULL) {
        self->callback(self->user, (float*)pOutput, frameCount);
    } else {
        /* Cisza jest jedyna bezpieczna odpowiedzia, gdy nie ma kto wypelnic bufora. */
        memset(pOutput, 0, (size_t)frameCount * pDevice->playback.channels * ma_get_bytes_per_sample(pDevice->playback.format));
    }
}

/*
    Zatrzymanie, ktorego nie zlecila aplikacja, oznacza utrate urzadzenia: odlaczenie
    sluchawek, uniewaznienie strumienia przez sterownik. Dla urzadzenia wybranego jawnie
    miniaudio nie przekierowuje strumienia, wiec bez tej wiadomosci warstwa zarzadzana
    uwazalaby, ze nadal gra.

    Pulapka: wywolanie przychodzi z watku roboczego miniaudio, wiec odbiorca nie moze tu
    zamykac ani zatrzymywac urzadzenia.
*/
static void cewka__device_notification(const ma_device_notification* pNotification)
{
    cewka_device* self = (cewka_device*)pNotification->pDevice->pUserData;
    if (self == NULL) {
        return;
    }

    if (pNotification->type == ma_device_notification_type_stopped && !self->stopRequested) {
        cewka_notify_cb notify = self->notify;
        if (notify != NULL) {
            notify(self->user, CEWKA_DEVICE_LOST);
        }
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
    config.dataCallback         = cewka__device_data;
    config.notificationCallback = cewka__device_notification;
    config.pUserData            = self;

    /* Urzadzenie stoi od utworzenia; jego zatrzymanie nie jest utrata. */
    self->stopRequested = 1;

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

/* Ustawia odbiorce zdarzen urzadzenia; NULL wylacza powiadamianie. */
CEWKA_API int cewka_device_set_notify(void* handle, cewka_notify_cb notify)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;
    ((cewka_device*)handle)->notify = notify;
    return (int)MA_SUCCESS;
}

CEWKA_API int cewka_device_start(void* handle)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;

    cewka_device* self = (cewka_device*)handle;
    self->stopRequested = 0;

    ma_result result = ma_device_start(&self->device);
    if (result != MA_SUCCESS) {
        self->stopRequested = 1;
    }

    return (int)result;
}

CEWKA_API int cewka_device_stop(void* handle)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;

    cewka_device* self = (cewka_device*)handle;
    self->stopRequested = 1;
    return (int)ma_device_stop(&self->device);
}

/* 1, gdy urzadzenie faktycznie pracuje; po utracie urzadzenia zwraca 0. */
CEWKA_API int cewka_device_is_started(void* handle)
{
    if (handle == NULL) return 0;
    return ma_device_is_started(&((cewka_device*)handle)->device) ? 1 : 0;
}

CEWKA_API void cewka_device_destroy(void* handle)
{
    if (handle == NULL) return;

    cewka_device* self = (cewka_device*)handle;
    self->stopRequested = 1;
    self->notify = NULL;
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

typedef size_t    (*cewka_read_cb)(void* user, void* buffer, size_t bytesToRead);
typedef int       (*cewka_seek_cb)(void* user, long long offset, int origin); /* 0 = poczatek, 1 = biezaca, 2 = koniec */
typedef long long (*cewka_tell_cb)(void* user, int what);                    /* 0 = polozenie, 1 = dlugosc; -1 przy bledzie */

/*
    Strumien podawany jest dekoderowi jako wirtualny system plikow, a nie przez
    ma_decoder_init. Tylko ta droga przekazuje funkcje "tell", bez ktorej dr_wav nie
    przycina naglowka do faktycznej dlugosci pliku (przerwane nagranie pokazywaloby
    dlugosc z naglowka), a dr_mp3 nie rozpoznaje znacznikow ID3v1 i APE na koncu.
*/
typedef struct
{
    ma_vfs_callbacks cb;
    void*            owner;
} cewka_stream_vfs;

typedef struct
{
    ma_decoder       decoder;
    cewka_stream_vfs vfs;
    cewka_read_cb    read;
    cewka_seek_cb    seek;
    cewka_tell_cb    tell;
    void*            user;
    int              seekTableTried;
} cewka_decoder;

static cewka_decoder* cewka__vfs_owner(ma_vfs* pVFS)
{
    return (cewka_decoder*)((cewka_stream_vfs*)pVFS)->owner;
}

static ma_result cewka__vfs_open(ma_vfs* pVFS, const char* pFilePath, ma_uint32 openMode, ma_vfs_file* pFile)
{
    (void)pFilePath;

    if ((openMode & MA_OPEN_MODE_WRITE) != 0) {
        return MA_ACCESS_DENIED;
    }

    /* Plik jest juz otwarty po stronie C#; uchwytem jest sam dekoder. */
    *pFile = (ma_vfs_file)cewka__vfs_owner(pVFS);
    return MA_SUCCESS;
}

static ma_result cewka__vfs_close(ma_vfs* pVFS, ma_vfs_file file)
{
    (void)pVFS;
    (void)file;
    return MA_SUCCESS;
}

static ma_result cewka__vfs_read(ma_vfs* pVFS, ma_vfs_file file, void* pDst, size_t sizeInBytes, size_t* pBytesRead)
{
    cewka_decoder* self = cewka__vfs_owner(pVFS);
    (void)file;

    size_t read = self->read(self->user, pDst, sizeInBytes);

    if (pBytesRead != NULL) {
        *pBytesRead = read;
    }

    return (read == 0 && sizeInBytes > 0) ? MA_AT_END : MA_SUCCESS;
}

static ma_result cewka__vfs_seek(ma_vfs* pVFS, ma_vfs_file file, ma_int64 offset, ma_seek_origin origin)
{
    cewka_decoder* self = cewka__vfs_owner(pVFS);
    (void)file;

    int code;
    switch (origin) {
        case ma_seek_origin_start:   code = 0; break;
        case ma_seek_origin_current: code = 1; break;
        case ma_seek_origin_end:     code = 2; break;
        default: return MA_INVALID_ARGS;
    }

    return self->seek(self->user, (long long)offset, code) ? MA_SUCCESS : MA_ERROR;
}

static ma_result cewka__vfs_tell(ma_vfs* pVFS, ma_vfs_file file, ma_int64* pCursor)
{
    cewka_decoder* self = cewka__vfs_owner(pVFS);
    (void)file;

    long long position = self->tell(self->user, 0);
    if (position < 0) {
        return MA_ERROR;
    }

    *pCursor = (ma_int64)position;
    return MA_SUCCESS;
}

static ma_result cewka__vfs_info(ma_vfs* pVFS, ma_vfs_file file, ma_file_info* pInfo)
{
    cewka_decoder* self = cewka__vfs_owner(pVFS);
    (void)file;

    long long length = self->tell(self->user, 1);
    if (length < 0) {
        return MA_NOT_IMPLEMENTED;
    }

    pInfo->sizeInBytes = (ma_uint64)length;
    return MA_SUCCESS;
}

/*
    Otwiera dekoder. outRate i outChannels rowne zeru oznaczaja format zrodlowy;
    podanie wartosci wlacza wbudowana konwersje miniaudio.

    lpfOrder dotyczy tej wbudowanej konwersji - MP3, FLAC i WAV resampluje
    dekoder miniaudio, a nie cewka_resampler_*. Znaczenie wartosci jak tam.
*/
CEWKA_API int cewka_decoder_open(cewka_read_cb read,
                                 cewka_seek_cb seek,
                                 cewka_tell_cb tell,
                                 void*         user,
                                 ma_uint32     outRate,
                                 ma_uint32     outChannels,
                                 ma_uint32     lpfOrder,
                                 void**        outHandle,
                                 ma_uint64*    outLengthFrames,
                                 ma_uint32*    outSourceRate,
                                 ma_uint32*    outSourceChannels)
{
    if (outHandle == NULL || read == NULL || seek == NULL || tell == NULL) {
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
    self->tell = tell;
    self->user = user;

    self->vfs.cb.onOpen  = cewka__vfs_open;
    self->vfs.cb.onClose = cewka__vfs_close;
    self->vfs.cb.onRead  = cewka__vfs_read;
    self->vfs.cb.onSeek  = cewka__vfs_seek;
    self->vfs.cb.onTell  = cewka__vfs_tell;
    self->vfs.cb.onInfo  = cewka__vfs_info;
    self->vfs.owner      = self;

    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, outChannels, outRate);

    if (lpfOrder > MA_MAX_FILTER_ORDER) {
        lpfOrder = MA_MAX_FILTER_ORDER;
    }
    config.resampling.linear.lpfOrder = lpfOrder;

    /* Sciezka bez rozszerzenia: format rozpoznaje sie po zawartosci, jak w ma_decoder_init. */
    ma_result result = ma_decoder_init_vfs((ma_vfs*)&self->vfs, "strumien", &config, &self->decoder);
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

/*
    Tablica punktow przewijania MP3, budowana przy pierwszym przewinieciu. Bez niej dr_mp3
    przewija, dekodujac wszystko od poczatku pliku, co przy dlugim nagraniu daje sekundy
    ciszy. Budowa przy otwarciu przeczytalaby caly plik w chwili przejscia miedzy utworami,
    gdy bufor ma zapas tylko pol sekundy, wiec odklada sie ja do pierwszej potrzeby.

    Pulapka: ma_dr_mp3_calculate_seek_points wraca na koniec na pozycje sprzed wyliczenia,
    przewijajac bez tablicy. Cofniecie strumienia na poczatek przed wyliczeniem czyni ten
    powrot darmowym; i tak zaraz nastepuje wlasciwe przewiniecie.
*/
static void cewka__ensure_mp3_seek_table(cewka_decoder* self)
{
    if (self->seekTableTried) {
        return;
    }
    self->seekTableTried = 1;

    if (self->decoder.pBackend == NULL || self->decoder.pBackendVTable != &g_ma_decoding_backend_vtable_mp3) {
        return;
    }

    ma_mp3* mp3 = (ma_mp3*)self->decoder.pBackend;
    if (mp3->seekPointCount > 0) {
        return;
    }

    ma_uint32 count = 1024;
    ma_dr_mp3_seek_point* points = (ma_dr_mp3_seek_point*)ma_malloc(sizeof(*points) * count, NULL);
    if (points == NULL) {
        return;
    }

    if (!ma_dr_mp3_seek_to_start_of_stream(&mp3->dr) ||
        !ma_dr_mp3_calculate_seek_points(&mp3->dr, &count, points) ||
        !ma_dr_mp3_bind_seek_table(&mp3->dr, count, points)) {
        ma_free(points, NULL);
        return;
    }

    /* ma_mp3_uninit zwalnia tablice razem z dekoderem. */
    mp3->seekPointCount = count;
    mp3->pSeekPoints    = points;
}

CEWKA_API int cewka_decoder_seek(void* handle, ma_uint64 frameIndex)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;

    cewka_decoder* self = (cewka_decoder*)handle;
    cewka__ensure_mp3_seek_table(self);
    return (int)ma_decoder_seek_to_pcm_frame(&self->decoder, frameIndex);
}

/* Polozenie dekodera w ramkach wyjsciowych; po nieudanym przewinieciu mowi, gdzie naprawde stoi. */
CEWKA_API ma_uint64 cewka_decoder_cursor(void* handle)
{
    if (handle == NULL) return 0;

    ma_uint64 cursor = 0;
    if (ma_decoder_get_cursor_in_pcm_frames(&((cewka_decoder*)handle)->decoder, &cursor) != MA_SUCCESS) {
        return 0;
    }

    return cursor;
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
