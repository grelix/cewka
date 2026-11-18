/*
    cewka_audio - cienka nakladka na miniaudio dla warstwy zarzadzanej.

    Powod istnienia: struktury konfiguracyjne miniaudio (ma_device_config, ma_decoder)
    sa duze i zmieniaja uklad miedzy wersjami. Odwzorowywanie ich w C# byloby krucha
    robota, ktora psulaby sie po kazdej aktualizacji biblioteki. Zamiast tego cala praca
    ze strukturami dzieje sie tutaj, a do C# wystawiony jest zestaw plaskich funkcji
    operujacych wylacznie na typach prostych i wskaznikach nieprzezroczystych.

    Zakres celowo waski. Warstwa zarzadzana odpowiada za caly tor przetwarzania sygnalu;
    stad potrzebne sa tylko: urzadzenie wyjsciowe, dekodery (miniaudio i WavPack) i wyliczenie urzadzen.

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

/* Sciezka do naglowka podaje skrypt budowy (wavpack/include). */
#include "wavpack.h"

#include <string.h>
#include <stdlib.h>
#include <stdio.h>

#if defined(_WIN32)
    #define CEWKA_API __declspec(dllexport)
#else
    #define CEWKA_API __attribute__((visibility("default")))
#endif

/*
    Wersja zestawu funkcji. Ta sama stala jest w NativeAudio.AbiVersion; zmienia sie przy kazdej
    zmianie sygnatur lub znaczenia eksportow. Biblioteka nie jest w repozytorium, wiec stara
    kopia moze trafic do nowego programu, a wywolanie funkcji o innej sygnaturze konczy sie
    awaria procesu zamiast bledu.
*/
#define CEWKA_ABI_VERSION 4

CEWKA_API int cewka_abi_version(void)
{
    return CEWKA_ABI_VERSION;
}

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
#define CEWKA_DEVICE_STOPPED_UNREQUESTED 1

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
    Zatrzymanie, ktorego nie zlecila aplikacja: odlaczenie sluchawek, uniewaznienie strumienia
    przez sterownik albo przekierowanie strumienia na nowe urzadzenie domyslne, po ktorym
    miniaudio samo uruchamia je ponownie. Rozroznia je warstwa zarzadzana, sprawdzajac po
    chwili cewka_device_is_started. Zgloszenie moze tez nie przyjsc wcale (nieudane zatrzymanie
    po odlaczeniu urzadzenia wybranego jawnie), dlatego warstwa zarzadzana sprawdza stan
    rowniez okresowo.

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
            notify(self->user, CEWKA_DEVICE_STOPPED_UNREQUESTED);
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

    lpfOrder to rzad filtru tej wbudowanej konwersji: 0 wylacza filtrowanie,
    8 to maksimum (MA_MAX_FILTER_ORDER).
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

/*
    Pulapka: ma_decoder_seek_to_pcm_frame aktualizuje kursor dekodera tylko przy powodzeniu,
    a zaplecze (dr_mp3, dr_flac) moglo sie w tym czasie przesunac. Po porazce kursor bierze sie
    wiec z zaplecza, przeliczony na ramki wyjsciowe.
*/
static void cewka__sync_cursor_with_backend(cewka_decoder* self)
{
    ma_uint64 backendCursor = 0;
    ma_uint32 backendRate = 0;

    if (self->decoder.pBackend == NULL) {
        return;
    }

    if (ma_data_source_get_cursor_in_pcm_frames(self->decoder.pBackend, &backendCursor) != MA_SUCCESS ||
        ma_data_source_get_data_format(self->decoder.pBackend, NULL, NULL, &backendRate, NULL, 0) != MA_SUCCESS) {
        return;
    }

    if (backendRate == 0 || backendRate == self->decoder.outputSampleRate) {
        self->decoder.readPointerInPCMFrames = backendCursor;
    } else {
        self->decoder.readPointerInPCMFrames =
            ma_calculate_frame_count_after_resampling(self->decoder.outputSampleRate, backendRate, backendCursor);
    }
}

CEWKA_API int cewka_decoder_seek(void* handle, ma_uint64 frameIndex)
{
    if (handle == NULL) return (int)MA_INVALID_ARGS;

    cewka_decoder* self = (cewka_decoder*)handle;
    cewka__ensure_mp3_seek_table(self);

    ma_result result = ma_decoder_seek_to_pcm_frame(&self->decoder, frameIndex);
    if (result != MA_SUCCESS) {
        cewka__sync_cursor_with_backend(self);
    }

    return (int)result;
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
   Dekoder MP3 na zywo

   Pchany ramka po ramce, bez wywolan zwrotnych. Odczyt przez onRead nie nadaje sie
   do strumienia: 0 bajtow ustawia trwaly koniec, dr_mp3 czyta na zapas do 64 KB,
   a przy zmianie kanalow miesza ramki. Tu format zna kazda ramka z osobna.
   ============================================================================ */

typedef struct
{
    ma_dr_mp3dec dec;
    ma_int16     pcm[MA_DR_MP3_MAX_SAMPLES_PER_FRAME];
} cewka_mp3_stream;

CEWKA_API int cewka_mp3_stream_open(void** outHandle)
{
    if (outHandle == NULL) {
        return (int)MA_INVALID_ARGS;
    }
    *outHandle = NULL;

    cewka_mp3_stream* self = (cewka_mp3_stream*)ma_malloc(sizeof(cewka_mp3_stream), NULL);
    if (self == NULL) {
        return (int)MA_OUT_OF_MEMORY;
    }

    memset(self, 0, sizeof(*self));
    ma_dr_mp3dec_init(&self->dec);

    *outHandle = self;
    return (int)MA_SUCCESS;
}

CEWKA_API void cewka_mp3_stream_reset(void* handle)
{
    if (handle == NULL) return;

    /* Bez zapamietanego naglowka nastepna ramka wyzeruje caly stan dekodera. */
    ma_dr_mp3dec_init(&((cewka_mp3_stream*)handle)->dec);
}

CEWKA_API void cewka_mp3_stream_close(void* handle)
{
    ma_free(handle, NULL);
}

/*
    Dekoduje najwyzej jedna ramke. Zwraca ramki PCM, -1 przy zlych argumentach.
    consumed = 0: za malo danych. consumed > 0 przy wyniku 0: smieci albo ramka bez rezerwy bitow.
    Pulapka: krotka porcja zostaje zjedzona jako smieci; wejscie co najmniej 6000 B.
*/
CEWKA_API int cewka_mp3_stream_decode(void*           handle,
                                      const ma_uint8* input,
                                      int             inputSize,
                                      float*          output,
                                      int             outputCapacitySamples,
                                      int*            consumed,
                                      int*            sampleRate,
                                      int*            channels,
                                      int*            bitrateKbps)
{
    if (handle == NULL || inputSize < 0 || (input == NULL && inputSize > 0) || output == NULL ||
        outputCapacitySamples < MA_DR_MP3_MAX_SAMPLES_PER_FRAME ||
        consumed == NULL || sampleRate == NULL || channels == NULL || bitrateKbps == NULL) {
        return -1;
    }

    *consumed = 0;
    *sampleRate = 0;
    *channels = 0;
    *bitrateKbps = 0;

    /* Pusta porcja zerowalaby stan dekodera tak samo jak smieci. */
    if (inputSize == 0) {
        return 0;
    }

    cewka_mp3_stream* self = (cewka_mp3_stream*)handle;

    ma_dr_mp3dec_frame_info info;
    memset(&info, 0, sizeof(info));

    int frames = ma_dr_mp3dec_decode_frame(&self->dec, input, inputSize, self->pcm, &info);

    *consumed = info.frame_bytes;
    if (frames <= 0) {
        return 0;
    }

    *sampleRate = info.sample_rate;
    *channels = info.channels;
    *bitrateKbps = info.bitrate_kbps;

    ma_dr_mp3_s16_to_f32(output, self->pcm, (ma_uint64)frames * (ma_uint64)info.channels);
    return frames;
}

/* ============================================================================
   Dekoder WavPack

   Biblioteka WavPack zbudowana tylko do odczytu: bez DSD, bez plikow sprzed wersji 4,
   bez wstawek asemblerowych i bez watkow. Strumien, jak przy dekoderze miniaudio,
   zostaje po stronie C#. Plik korekcyjny .wvc jest pomijany: biblioteka dostaje tylko
   strumien glowny, wiec plik hybrydowy gra czesc stratna.
   ============================================================================ */

#define CEWKA_WAVPACK_OK            0
#define CEWKA_WAVPACK_INVALID_ARGS  1
#define CEWKA_WAVPACK_OUT_OF_MEMORY 2
#define CEWKA_WAVPACK_DAMAGED       3
#define CEWKA_WAVPACK_UNSUPPORTED   4   /* DSD albo plik sprzed wersji 4 */
#define CEWKA_WAVPACK_CHANNELS      5   /* liczba kanalow poza granica */
#define CEWKA_WAVPACK_SEEK_FAILED   6

/* Granice wartosci z pliku. Bufor posredni rosnie z liczba kanalow. */
#define CEWKA_WAVPACK_MAX_CHANNELS  32
#define CEWKA_WAVPACK_MAX_RATE      768000
#define CEWKA_WAVPACK_CHUNK_FRAMES  4096

typedef struct
{
    WavpackContext*       wpc;
    WavpackStreamReader64 reader;
    cewka_read_cb         read;
    cewka_seek_cb         seek;
    cewka_tell_cb         tell;
    void*                 user;
    int                   pushedBack;   /* bajt cofniety przez biblioteke albo -1 */
    int                   channels;
    int                   isFloat;
    double                scale;        /* probka calkowita do zakresu +-1 */
    long long             totalFrames;  /* -1, gdy plik nie podaje dlugosci */
    long long             position;
    int                   atEnd;        /* przewinieto za koniec bez udzialu biblioteki */
    int                   broken;       /* po nieudanym przewinieciu biblioteki nie wolno juz czytac */
    int32_t*              chunk;
} cewka_wavpack;

static int32_t cewka__wv_read_bytes(void* id, void* data, int32_t bcount)
{
    cewka_wavpack* self = (cewka_wavpack*)id;
    unsigned char* out = (unsigned char*)data;
    int32_t total = 0;

    if (bcount <= 0) {
        return 0;
    }

    if (self->pushedBack >= 0) {
        *out++ = (unsigned char)self->pushedBack;
        self->pushedBack = -1;
        total = 1;
    }

    /* Strumien zarzadzany oddaje czasem mniej, niz poproszono, a biblioteka czyta krotsza porcje jako koniec pliku. */
    while (total < bcount) {
        size_t wanted = (size_t)(bcount - total);
        size_t got = self->read(self->user, out, wanted);
        if (got == 0 || got > wanted) {
            break;
        }
        out += got;
        total += (int32_t)got;
    }

    return total;
}

static int32_t cewka__wv_write_bytes(void* id, void* data, int32_t bcount)
{
    (void)id;
    (void)data;
    (void)bcount;
    return 0;
}

static int64_t cewka__wv_get_pos(void* id)
{
    cewka_wavpack* self = (cewka_wavpack*)id;
    long long position = self->tell(self->user, 0);

    if (position < 0) {
        return -1;
    }

    return self->pushedBack >= 0 ? position - 1 : position;
}

static int cewka__wv_set_pos_abs(void* id, int64_t pos)
{
    cewka_wavpack* self = (cewka_wavpack*)id;
    self->pushedBack = -1;

    return (pos >= 0 && self->seek(self->user, (long long)pos, 0)) ? 0 : -1;
}

static int cewka__wv_set_pos_rel(void* id, int64_t delta, int mode)
{
    cewka_wavpack* self = (cewka_wavpack*)id;
    int origin;

    switch (mode) {
        case SEEK_SET: origin = 0; break;
        case SEEK_CUR:
            origin = 1;
            /* Cofniety bajt zostal juz odczytany ze strumienia. */
            if (self->pushedBack >= 0) delta -= 1;
            break;
        case SEEK_END: origin = 2; break;
        default: return -1;
    }

    self->pushedBack = -1;
    return self->seek(self->user, (long long)delta, origin) ? 0 : -1;
}

static int cewka__wv_push_back_byte(void* id, int c)
{
    cewka_wavpack* self = (cewka_wavpack*)id;

    if (c == EOF) {
        return EOF;
    }

    self->pushedBack = c & 0xFF;
    return c;
}

static int64_t cewka__wv_get_length(void* id)
{
    cewka_wavpack* self = (cewka_wavpack*)id;
    long long length = self->tell(self->user, 1);
    return length < 0 ? 0 : length;
}

static int cewka__wv_can_seek(void* id)
{
    cewka_wavpack* self = (cewka_wavpack*)id;
    return self->tell(self->user, 1) >= 0;
}

static int cewka__wv_truncate_here(void* id)
{
    (void)id;
    return -1;
}

static int cewka__wv_close(void* id)
{
    (void)id;
    return 0;
}

static void cewka__wv_copy_error(const char* message, char* error, int errorSize)
{
    size_t length;

    if (error == NULL || errorSize <= 0) {
        return;
    }

    length = strlen(message);
    if (length >= (size_t)errorSize) {
        length = (size_t)errorSize - 1;
    }

    memcpy(error, message, length);
    error[length] = '\0';
}

static void cewka__wv_free(cewka_wavpack* self)
{
    if (self->wpc != NULL) {
        WavpackCloseFile(self->wpc);
    }
    ma_free(self->chunk, NULL);
    ma_free(self, NULL);
}

/*
    Otwiera plik WavPack. W error trafia opis bledu biblioteki (do dziennika, nie na ekran).
    outChannelMask to maska kanalow WAVE, zero gdy plik jej nie podaje. outTotalFrames
    rowne -1 oznacza nieznana dlugosc.
*/
CEWKA_API int cewka_wavpack_open(cewka_read_cb read,
                                 cewka_seek_cb seek,
                                 cewka_tell_cb tell,
                                 void*         user,
                                 char*         error,
                                 int           errorSize,
                                 void**        outHandle,
                                 ma_uint32*    outRate,
                                 ma_uint32*    outChannels,
                                 ma_uint32*    outChannelMask,
                                 ma_uint32*    outBits,
                                 int*          outFloat,
                                 long long*    outTotalFrames)
{
    /* Biblioteka zapisuje opis bledu do 80 znakow. */
    char message[128];
    cewka_wavpack* self;
    int channels;
    int bytes;
    ma_uint32 rate;

    if (read == NULL || seek == NULL || tell == NULL || outHandle == NULL || outRate == NULL ||
        outChannels == NULL || outChannelMask == NULL || outBits == NULL || outFloat == NULL ||
        outTotalFrames == NULL) {
        return CEWKA_WAVPACK_INVALID_ARGS;
    }

    *outHandle = NULL;
    cewka__wv_copy_error("", error, errorSize);

    self = (cewka_wavpack*)ma_malloc(sizeof(cewka_wavpack), NULL);
    if (self == NULL) {
        return CEWKA_WAVPACK_OUT_OF_MEMORY;
    }
    memset(self, 0, sizeof(*self));

    self->read = read;
    self->seek = seek;
    self->tell = tell;
    self->user = user;
    self->pushedBack = -1;

    self->reader.read_bytes     = cewka__wv_read_bytes;
    self->reader.write_bytes    = cewka__wv_write_bytes;
    self->reader.get_pos        = cewka__wv_get_pos;
    self->reader.set_pos_abs    = cewka__wv_set_pos_abs;
    self->reader.set_pos_rel    = cewka__wv_set_pos_rel;
    self->reader.push_back_byte = cewka__wv_push_back_byte;
    self->reader.get_length     = cewka__wv_get_length;
    self->reader.can_seek       = cewka__wv_can_seek;
    self->reader.truncate_here  = cewka__wv_truncate_here;
    self->reader.close          = cewka__wv_close;

    /*
        OPEN_NORMALIZE: probki zmiennoprzecinkowe w zakresie +-1 bez wzgledu na wykladnik zapisany
        w pliku. Bez OPEN_WVC plik korekcyjny jest pomijany, bez OPEN_TAGS biblioteka nie czyta
        znacznikow (czyta je warstwa zarzadzana).
    */
    message[0] = '\0';
    self->wpc = WavpackOpenFileInputEx64(&self->reader, self, NULL, message, OPEN_NORMALIZE, 0);

    if (self->wpc == NULL) {
        /* Biblioteka zbudowana bez DSD i bez plikow sprzed wersji 4 odrzuca je z takim opisem. */
        int code = (strstr(message, "DSD") != NULL || strstr(message, "legacy") != NULL)
            ? CEWKA_WAVPACK_UNSUPPORTED
            : CEWKA_WAVPACK_DAMAGED;

        cewka__wv_copy_error(message, error, errorSize);
        cewka__wv_free(self);
        return code;
    }

    channels = WavpackGetNumChannels(self->wpc);
    bytes = WavpackGetBytesPerSample(self->wpc);
    rate = WavpackGetSampleRate(self->wpc);

    if ((WavpackGetQualifyMode(self->wpc) & QMODE_DSD_AUDIO) != 0) {
        cewka__wv_copy_error("DSD", error, errorSize);
        cewka__wv_free(self);
        return CEWKA_WAVPACK_UNSUPPORTED;
    }

    if (channels < 1 || channels > CEWKA_WAVPACK_MAX_CHANNELS) {
        cewka__wv_copy_error("channel count out of range", error, errorSize);
        cewka__wv_free(self);
        return CEWKA_WAVPACK_CHANNELS;
    }

    self->isFloat = (WavpackGetMode(self->wpc) & MODE_FLOAT) != 0;

    if (rate == 0 || rate > CEWKA_WAVPACK_MAX_RATE || bytes < 1 || bytes > 4 || (self->isFloat && bytes != 4)) {
        cewka__wv_copy_error("sample format out of range", error, errorSize);
        cewka__wv_free(self);
        return CEWKA_WAVPACK_DAMAGED;
    }

    self->chunk = (int32_t*)ma_malloc(sizeof(int32_t) * CEWKA_WAVPACK_CHUNK_FRAMES * (size_t)channels, NULL);
    if (self->chunk == NULL) {
        cewka__wv_free(self);
        return CEWKA_WAVPACK_OUT_OF_MEMORY;
    }

    /* Probki calkowite przychodza wyrownane do prawej w szerokosci pojemnika (bytes * 8 bitow). */
    self->channels = channels;
    self->scale = 1.0 / (double)((long long)1 << (bytes * 8 - 1));
    self->totalFrames = (long long)WavpackGetNumSamples64(self->wpc);
    if (self->totalFrames < 0) {
        self->totalFrames = -1;
    }

    *outRate = rate;
    *outChannels = (ma_uint32)channels;
    *outChannelMask = (ma_uint32)WavpackGetChannelMask(self->wpc);
    *outBits = (ma_uint32)WavpackGetBitsPerSample(self->wpc);
    *outFloat = self->isFloat;
    *outTotalFrames = self->totalFrames;
    *outHandle = self;
    return CEWKA_WAVPACK_OK;
}

/* Zwraca liczbe ramek float32 z przeplotem kanalow; zero oznacza koniec, -1 zle argumenty. */
CEWKA_API int cewka_wavpack_read(void* handle, float* output, int frameCount)
{
    cewka_wavpack* self = (cewka_wavpack*)handle;
    int produced = 0;

    if (self == NULL || output == NULL || frameCount < 0) {
        return -1;
    }

    if (self->atEnd || self->broken) {
        return 0;
    }

    while (produced < frameCount) {
        int remaining = frameCount - produced;
        uint32_t wanted = (uint32_t)(remaining < CEWKA_WAVPACK_CHUNK_FRAMES ? remaining : CEWKA_WAVPACK_CHUNK_FRAMES);
        uint32_t got = WavpackUnpackSamples(self->wpc, self->chunk, wanted);
        size_t count;
        float* dst;
        size_t i;

        if (got == 0 || got > wanted) {
            break;
        }

        count = (size_t)got * (size_t)self->channels;
        dst = output + (size_t)produced * (size_t)self->channels;

        if (self->isFloat) {
            /* Dla plikow zmiennoprzecinkowych biblioteka oddaje bity liczby float w int32. */
            memcpy(dst, self->chunk, count * sizeof(float));
        } else {
            for (i = 0; i < count; i++) {
                dst[i] = (float)((double)self->chunk[i] * self->scale);
            }
        }

        produced += (int)got;

        if (got < wanted) {
            break;
        }
    }

    self->position += produced;
    return produced;
}

/*
    Przewija do ramki. Za koncem pliku biblioteka nie jest pytana, tylko dekoder staje na koncu.
    Po nieudanym przewinieciu biblioteka nie nadaje sie do odczytu, wiec dekoder milknie.
*/
CEWKA_API int cewka_wavpack_seek(void* handle, long long frame)
{
    cewka_wavpack* self = (cewka_wavpack*)handle;

    if (self == NULL || frame < 0) {
        return CEWKA_WAVPACK_INVALID_ARGS;
    }

    if (self->broken) {
        return CEWKA_WAVPACK_SEEK_FAILED;
    }

    if (self->totalFrames >= 0 && frame >= self->totalFrames) {
        self->atEnd = 1;
        self->position = self->totalFrames;
        return CEWKA_WAVPACK_OK;
    }

    if (!WavpackSeekSample64(self->wpc, (int64_t)frame)) {
        self->broken = 1;
        return CEWKA_WAVPACK_SEEK_FAILED;
    }

    self->atEnd = 0;
    self->position = frame;
    return CEWKA_WAVPACK_OK;
}

CEWKA_API long long cewka_wavpack_position(void* handle)
{
    return handle == NULL ? 0 : ((cewka_wavpack*)handle)->position;
}

CEWKA_API void cewka_wavpack_close(void* handle)
{
    if (handle == NULL) return;
    cewka__wv_free((cewka_wavpack*)handle);
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
