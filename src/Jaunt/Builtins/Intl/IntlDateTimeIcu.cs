using System;

namespace Jaunt.Runtime;

/// <summary>
/// ICU bindings used by Intl.DateTimeFormat (udat, udatpg, udtitvfmt, ucal, ufmtval, unumsys). Kept in a class of their
/// own so that other Intl services can bind the same functions without member clashes.
/// </summary>
internal static unsafe class IcuDtf
{
    private static nint P(ref nint slot, string name) => Icu.P(ref slot, name);

    private static nint s_udatOpen, s_udatClose, s_udatToPattern, s_udatFormat, s_udatFormatForFields, s_udatGetCalendar, s_udatSetCalendar;
    private static nint s_ucalClone, s_ucalClose, s_ucalOpen, s_ucalSetGregorianChange, s_ucalSetMillis, s_ucalGetKeywordValuesForLocale;
    private static nint s_ufpOpen, s_ufpClose, s_ufpNext;
    private static nint s_dtpgOpen, s_dtpgClose, s_dtpgBest, s_dtpgSkeleton, s_dtpgDefaultHc;
    private static nint s_itvOpen, s_itvClose, s_itvOpenResult, s_itvCloseResult, s_itvAsValue, s_itvFormatCal;
    private static nint s_fvGetString, s_fvNextPosition, s_cfpOpen, s_cfpClose, s_cfpGetCategory, s_cfpGetField, s_cfpGetIndexes;
    private static nint s_ucalOpenTimeZones;
    public static nint ucal_openTimeZones(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_ucalOpenTimeZones, "ucal_openTimeZones"))(err);

    private static nint s_nsOpen, s_nsOpenByName, s_nsClose, s_nsGetName, s_nsIsAlgorithmic, s_nsAvailable;

    public const int UDAT_FULL = 0, UDAT_LONG = 1, UDAT_MEDIUM = 2, UDAT_SHORT = 3, UDAT_NONE = -1, UDAT_PATTERN = -2;

    public static nint udat_open(int timeStyle, int dateStyle, byte* locale, char* tzId, int tzLen, char* pattern, int patternLen, int* err) =>
        ((delegate* unmanaged<int, int, byte*, char*, int, char*, int, int*, nint>)P(ref s_udatOpen, "udat_open"))(timeStyle, dateStyle, locale, tzId, tzLen, pattern, patternLen, err);
    public static void udat_close(nint f) => ((delegate* unmanaged<nint, void>)P(ref s_udatClose, "udat_close"))(f);
    public static int udat_toPattern(nint f, sbyte localized, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, sbyte, char*, int, int*, int>)P(ref s_udatToPattern, "udat_toPattern"))(f, localized, buf, cap, err);
    public static int udat_format(nint f, double date, char* buf, int cap, nint pos, int* err) =>
        ((delegate* unmanaged<nint, double, char*, int, nint, int*, int>)P(ref s_udatFormat, "udat_format"))(f, date, buf, cap, pos, err);
    public static int udat_formatForFields(nint f, double date, char* buf, int cap, nint iter, int* err) =>
        ((delegate* unmanaged<nint, double, char*, int, nint, int*, int>)P(ref s_udatFormatForFields, "udat_formatForFields"))(f, date, buf, cap, iter, err);
    public static nint udat_getCalendar(nint f) => ((delegate* unmanaged<nint, nint>)P(ref s_udatGetCalendar, "udat_getCalendar"))(f);
    public static void udat_setCalendar(nint f, nint cal) => ((delegate* unmanaged<nint, nint, void>)P(ref s_udatSetCalendar, "udat_setCalendar"))(f, cal);

    public static nint ucal_clone(nint cal, int* err) => ((delegate* unmanaged<nint, int*, nint>)P(ref s_ucalClone, "ucal_clone"))(cal, err);
    public static void ucal_close(nint cal) => ((delegate* unmanaged<nint, void>)P(ref s_ucalClose, "ucal_close"))(cal);
    public static nint ucal_open(char* zone, int len, byte* locale, int type, int* err) =>
        ((delegate* unmanaged<char*, int, byte*, int, int*, nint>)P(ref s_ucalOpen, "ucal_open"))(zone, len, locale, type, err);
    public static void ucal_setGregorianChange(nint cal, double date, int* err) =>
        ((delegate* unmanaged<nint, double, int*, void>)P(ref s_ucalSetGregorianChange, "ucal_setGregorianChange"))(cal, date, err);
    public static void ucal_setMillis(nint cal, double date, int* err) =>
        ((delegate* unmanaged<nint, double, int*, void>)P(ref s_ucalSetMillis, "ucal_setMillis"))(cal, date, err);
    public static nint ucal_getKeywordValuesForLocale(byte* key, byte* locale, sbyte commonlyUsed, int* err) =>
        ((delegate* unmanaged<byte*, byte*, sbyte, int*, nint>)P(ref s_ucalGetKeywordValuesForLocale, "ucal_getKeywordValuesForLocale"))(key, locale, commonlyUsed, err);

    public static nint ufieldpositer_open(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_ufpOpen, "ufieldpositer_open"))(err);
    public static void ufieldpositer_close(nint it) => ((delegate* unmanaged<nint, void>)P(ref s_ufpClose, "ufieldpositer_close"))(it);
    public static int ufieldpositer_next(nint it, int* begin, int* end) =>
        ((delegate* unmanaged<nint, int*, int*, int>)P(ref s_ufpNext, "ufieldpositer_next"))(it, begin, end);

    public static nint udatpg_open(byte* locale, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_dtpgOpen, "udatpg_open"))(locale, err);
    public static void udatpg_close(nint g) => ((delegate* unmanaged<nint, void>)P(ref s_dtpgClose, "udatpg_close"))(g);
    public static int udatpg_getBestPatternWithOptions(nint g, char* skeleton, int len, int options, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, char*, int, int, char*, int, int*, int>)P(ref s_dtpgBest, "udatpg_getBestPatternWithOptions"))(g, skeleton, len, options, buf, cap, err);
    public static int udatpg_getSkeleton(nint g, char* pattern, int len, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, char*, int, char*, int, int*, int>)P(ref s_dtpgSkeleton, "udatpg_getSkeleton"))(g, pattern, len, buf, cap, err);
    public static int udatpg_getDefaultHourCycle(nint g, int* err) =>
        ((delegate* unmanaged<nint, int*, int>)P(ref s_dtpgDefaultHc, "udatpg_getDefaultHourCycle"))(g, err);

    public static nint udtitvfmt_open(byte* locale, char* skeleton, int len, char* tz, int tzLen, int* err) =>
        ((delegate* unmanaged<byte*, char*, int, char*, int, int*, nint>)P(ref s_itvOpen, "udtitvfmt_open"))(locale, skeleton, len, tz, tzLen, err);
    public static void udtitvfmt_close(nint f) => ((delegate* unmanaged<nint, void>)P(ref s_itvClose, "udtitvfmt_close"))(f);
    public static nint udtitvfmt_openResult(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_itvOpenResult, "udtitvfmt_openResult"))(err);
    public static void udtitvfmt_closeResult(nint r) => ((delegate* unmanaged<nint, void>)P(ref s_itvCloseResult, "udtitvfmt_closeResult"))(r);
    public static nint udtitvfmt_resultAsValue(nint r, int* err) => ((delegate* unmanaged<nint, int*, nint>)P(ref s_itvAsValue, "udtitvfmt_resultAsValue"))(r, err);
    public static void udtitvfmt_formatCalendarToResult(nint f, nint from, nint to, nint result, int* err) =>
        ((delegate* unmanaged<nint, nint, nint, nint, int*, void>)P(ref s_itvFormatCal, "udtitvfmt_formatCalendarToResult"))(f, from, to, result, err);

    public static char* ufmtval_getString(nint v, int* len, int* err) => ((delegate* unmanaged<nint, int*, int*, char*>)P(ref s_fvGetString, "ufmtval_getString"))(v, len, err);
    public static sbyte ufmtval_nextPosition(nint v, nint cfpos, int* err) => ((delegate* unmanaged<nint, nint, int*, sbyte>)P(ref s_fvNextPosition, "ufmtval_nextPosition"))(v, cfpos, err);
    public static nint ucfpos_open(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_cfpOpen, "ucfpos_open"))(err);
    public static void ucfpos_close(nint p) => ((delegate* unmanaged<nint, void>)P(ref s_cfpClose, "ucfpos_close"))(p);
    public static int ucfpos_getCategory(nint p, int* err) => ((delegate* unmanaged<nint, int*, int>)P(ref s_cfpGetCategory, "ucfpos_getCategory"))(p, err);
    public static int ucfpos_getField(nint p, int* err) => ((delegate* unmanaged<nint, int*, int>)P(ref s_cfpGetField, "ucfpos_getField"))(p, err);
    public static void ucfpos_getIndexes(nint p, int* start, int* limit, int* err) =>
        ((delegate* unmanaged<nint, int*, int*, int*, void>)P(ref s_cfpGetIndexes, "ucfpos_getIndexes"))(p, start, limit, err);

    public static nint unumsys_open(byte* locale, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_nsOpen, "unumsys_open"))(locale, err);
    public static nint unumsys_openByName(byte* name, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_nsOpenByName, "unumsys_openByName"))(name, err);
    public static void unumsys_close(nint ns) => ((delegate* unmanaged<nint, void>)P(ref s_nsClose, "unumsys_close"))(ns);
    public static byte* unumsys_getName(nint ns) => ((delegate* unmanaged<nint, byte*>)P(ref s_nsGetName, "unumsys_getName"))(ns);
    public static sbyte unumsys_isAlgorithmic(nint ns) => ((delegate* unmanaged<nint, sbyte>)P(ref s_nsIsAlgorithmic, "unumsys_isAlgorithmic"))(ns);
    public static nint unumsys_openAvailableNames(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_nsAvailable, "unumsys_openAvailableNames"))(err);
}
