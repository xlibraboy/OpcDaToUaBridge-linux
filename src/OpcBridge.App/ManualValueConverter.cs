using System.Globalization;

namespace OpcBridge.App;

/// <summary>
/// Converts a tag's stored Manual Value text into a typed CLR value for Manual
/// (simulation) mode.
/// </summary>
/// <remarks>
/// Parsing honours the mapping's declared DataType first (a parse failure on a declared
/// type rejects the manual value rather than re-typing it). When the mapping type is
/// "Auto" and a real value already exists for the tag (the tag was read from its source
/// before simulation was switched on), the manual text is instead parsed into that value's
/// runtime type — the same type Live Values / Maps / the faceplate display — so publishing
/// a manual value can no longer silently change the tag's type (whole numbers became
/// Int64, arbitrary text became String). Generic text inference remains the fallback only
/// for tags that never had a real value (e.g. a mapping created straight into Manual mode).
/// </remarks>
internal static class ManualValueConverter
{
    /// <summary>
    /// Tries to parse <paramref name="manualValue"/> into a typed CLR value, following
    /// <paramref name="dataType"/> when it names a concrete type. For "Auto" mappings the
    /// runtime type of <paramref name="referenceValue"/> (the tag's current value, when any)
    /// pins the target type; otherwise the text is inferred generically.
    /// </summary>
    public static bool TryConvert(string? dataType, string? manualValue, object? referenceValue, out object? convertedValue)
    {
        string text = manualValue?.Trim() ?? string.Empty;
        string normalizedDataType = dataType.Trim().ToUpperInvariant();

        // "Auto" mapping with a live value: pin the parse to the tag's actual type so the
        // manual value keeps following it. No live value → legacy content inference below.
        bool isAuto = normalizedDataType is "" or "AUTO";
        if (isAuto && referenceValue is not null)
        {
            string? actualType = DashboardValues.InferDataType(referenceValue);
            if (actualType is not null &&
                !string.Equals(actualType, "ByteString", StringComparison.OrdinalIgnoreCase))
            {
                normalizedDataType = actualType.ToUpperInvariant();
            }
            else
            {
                return TryInfer(text, out convertedValue);
            }
        }

        if (normalizedDataType is "STRING")
        {
            convertedValue = text;
            return true;
        }

        if (normalizedDataType is "BOOL" or "BOOLEAN")
        {
            if (bool.TryParse(text, out bool boolValue))
            {
                convertedValue = boolValue;
                return true;
            }

            if (text == "1")
            {
                convertedValue = true;
                return true;
            }

            if (text == "0")
            {
                convertedValue = false;
                return true;
            }

            convertedValue = null;
            return false;
        }

        if (normalizedDataType is "BYTE")
        {
            if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte byteValue))
            {
                convertedValue = byteValue;
                return true;
            }
        }
        else if (normalizedDataType is "SBYTE")
        {
            if (sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte sbyteValue))
            {
                convertedValue = sbyteValue;
                return true;
            }
        }
        else if (normalizedDataType is "INT16" or "SHORT")
        {
            if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short shortValue))
            {
                convertedValue = shortValue;
                return true;
            }
        }
        else if (normalizedDataType is "UINT16")
        {
            if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort ushortValue))
            {
                convertedValue = ushortValue;
                return true;
            }
        }
        else if (normalizedDataType is "INT32" or "INT")
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
            {
                convertedValue = intValue;
                return true;
            }
        }
        else if (normalizedDataType is "UINT32")
        {
            if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uintValue))
            {
                convertedValue = uintValue;
                return true;
            }
        }
        else if (normalizedDataType is "INT64" or "LONG")
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
            {
                convertedValue = longValue;
                return true;
            }
        }
        else if (normalizedDataType is "UINT64")
        {
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong ulongValue))
            {
                convertedValue = ulongValue;
                return true;
            }
        }
        else if (normalizedDataType is "FLOAT" or "SINGLE")
        {
            if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float floatValue))
            {
                convertedValue = floatValue;
                return true;
            }
        }
        else if (normalizedDataType is "DOUBLE" or "REAL8")
        {
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
            {
                convertedValue = doubleValue;
                return true;
            }
        }
        else if (normalizedDataType is "DECIMAL")
        {
            if (decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal decimalValue))
            {
                convertedValue = decimalValue;
                return true;
            }
        }
        else if (normalizedDataType is "DATETIME")
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dateTimeValue))
            {
                convertedValue = dateTimeValue;
                return true;
            }
        }
        // Unrecognized declared type: keep the legacy behaviour of inferring from the text.
        else if (TryInfer(text, out convertedValue))
        {
            return true;
        }

        convertedValue = null;
        return false;
    }

    /// <summary>
    /// Legacy content-based inference (bool → integer → floating point → string), used when
    /// the mapping declares Auto and no real value exists to pin the actual type, or the
    /// declared type name is unrecognized.
    /// </summary>
    private static bool TryInfer(string text, out object? convertedValue)
    {
        if (bool.TryParse(text, out bool boolValue))
        {
            convertedValue = boolValue;
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
        {
            convertedValue = longValue;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
        {
            convertedValue = doubleValue;
            return true;
        }

        convertedValue = text;
        return true;
    }
}
