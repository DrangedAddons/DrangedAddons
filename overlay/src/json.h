// A small JSON reader and writer for the pipe messages: objects, arrays, strings, numbers, true, false, null.
#pragma once
#include <string>
#include <utility>
#include <vector>

namespace json {

struct Value
{
    enum Kind { Null, Bool, Number, String, Array, Object } kind = Null;
    bool b = false;
    double n = 0;
    std::string s;
    std::vector<Value> items;
    std::vector<std::pair<std::string, Value>> fields;

    const Value& operator[](const char* key) const
    {
        static const Value none;
        for (auto& [k, v] : fields)
            if (k == key) return v;
        return none;
    }
    const std::string& str() const { return s; }
    int num(int fallback = 0) const { return kind == Number ? (int)n : fallback; }
    bool flag() const { return kind == Bool && b; }
};

namespace detail {

inline void Space(const char*& p, const char* end)
{
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
}

inline void Utf8(std::string& out, unsigned c)
{
    if (c < 0x80) out += (char)c;
    else if (c < 0x800) { out += (char)(0xC0 | c >> 6); out += (char)(0x80 | (c & 0x3F)); }
    else if (c < 0x10000) { out += (char)(0xE0 | c >> 12); out += (char)(0x80 | (c >> 6 & 0x3F)); out += (char)(0x80 | (c & 0x3F)); }
    else { out += (char)(0xF0 | c >> 18); out += (char)(0x80 | (c >> 12 & 0x3F)); out += (char)(0x80 | (c >> 6 & 0x3F)); out += (char)(0x80 | (c & 0x3F)); }
}

inline unsigned Hex4(const char*& p, const char* end)
{
    unsigned v = 0;
    for (int i = 0; i < 4 && p < end; i++, p++)
    {
        char c = *p;
        v = v * 16 + (c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0);
    }
    return v;
}

inline bool ParseString(const char*& p, const char* end, std::string& out)
{
    if (p >= end || *p != '"') return false;
    p++;
    while (p < end && *p != '"')
    {
        if (*p != '\\') { out += *p++; continue; }
        if (++p >= end) return false;
        switch (*p++)
        {
        case 'n': out += '\n'; break;
        case 't': out += '\t'; break;
        case 'r': out += '\r'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case 'u':
        {
            unsigned c = Hex4(p, end);
            if (c >= 0xD800 && c < 0xDC00 && p + 6 <= end && p[0] == '\\' && p[1] == 'u')
            {
                p += 2;
                unsigned low = Hex4(p, end);
                c = 0x10000 + ((c - 0xD800) << 10) + (low - 0xDC00);
            }
            Utf8(out, c);
            break;
        }
        default: out += p[-1]; break; // \" \\ \/
        }
    }
    if (p >= end) return false;
    p++;
    return true;
}

inline bool Parse(const char*& p, const char* end, Value& v, int depth)
{
    if (depth > 32) return false;
    Space(p, end);
    if (p >= end) return false;
    switch (*p)
    {
    case '{':
        v.kind = Value::Object;
        p++;
        Space(p, end);
        if (p < end && *p == '}') { p++; return true; }
        while (true)
        {
            std::string key;
            Space(p, end);
            if (!ParseString(p, end, key)) return false;
            Space(p, end);
            if (p >= end || *p++ != ':') return false;
            Value item;
            if (!Parse(p, end, item, depth + 1)) return false;
            v.fields.emplace_back(std::move(key), std::move(item));
            Space(p, end);
            if (p < end && *p == ',') { p++; continue; }
            if (p < end && *p == '}') { p++; return true; }
            return false;
        }
    case '[':
        v.kind = Value::Array;
        p++;
        Space(p, end);
        if (p < end && *p == ']') { p++; return true; }
        while (true)
        {
            Value item;
            if (!Parse(p, end, item, depth + 1)) return false;
            v.items.push_back(std::move(item));
            Space(p, end);
            if (p < end && *p == ',') { p++; continue; }
            if (p < end && *p == ']') { p++; return true; }
            return false;
        }
    case '"':
        v.kind = Value::String;
        return ParseString(p, end, v.s);
    case 't':
        if (end - p < 4) return false;
        p += 4;
        v.kind = Value::Bool;
        v.b = true;
        return true;
    case 'f':
        if (end - p < 5) return false;
        p += 5;
        v.kind = Value::Bool;
        return true;
    case 'n':
        if (end - p < 4) return false;
        p += 4;
        return true;
    default:
    {
        char* stop = nullptr;
        std::string number;
        while (p < end && (*p == '-' || *p == '+' || *p == '.' || *p == 'e' || *p == 'E' || (*p >= '0' && *p <= '9'))) number += *p++;
        if (number.empty()) return false;
        v.kind = Value::Number;
        v.n = strtod(number.c_str(), &stop);
        return true;
    }
    }
}

} // namespace detail

inline bool Parse(const std::string& text, Value& out)
{
    const char* p = text.data();
    return detail::Parse(p, p + text.size(), out, 0);
}

/// A string as a JSON string literal, quotes included.
inline std::string Quote(const std::string& s)
{
    std::string out = "\"";
    for (unsigned char c : s)
    {
        if (c == '"' || c == '\\') { out += '\\'; out += (char)c; }
        else if (c == '\n') out += "\\n";
        else if (c < 0x20)
        {
            char buf[8];
            snprintf(buf, sizeof buf, "\\u%04x", c);
            out += buf;
        }
        else out += (char)c;
    }
    return out + "\"";
}

} // namespace json
