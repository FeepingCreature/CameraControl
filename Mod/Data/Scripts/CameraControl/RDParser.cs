using System;
using System.Collections.Generic;

public class RDParser
{
    string text;
    int offset = 0;
    Stack<int> offset_stack;

    public RDParser(string text)
    {
        this.text = text;
        this.offset_stack = new Stack<int>();
    }

    public void Clean()
    {
        while (offset < text.Length && (text[offset] == ' ' || text[offset] == '\n')) offset++;
    }

    public void BeginTransaction()
    {
        offset_stack.Push(offset);
    }

    public void AbortTransaction()
    {
        offset = offset_stack.Pop();
    }

    public void CommitTransaction()
    {
        offset_stack.Pop();
    }

    public void Fail(string msg)
    {
        // TODO line numbers?
        throw new Exception(msg + " at " + text.Substring(offset, Math.Min(text.Length - offset, 32)));
    }

    public void End()
    {
        if (offset_stack.Count != 0) Fail("Parsing ended, but transactions were still open.");
        Clean();
        if (offset < text.Length) Fail("Parsing ended, but text was left over");
    }

    public bool StartsWith(string cmp)
    {
        if (text.Length - offset < cmp.Length) return false;
        for (int i = 0; i < cmp.Length; i++)
        {
            if (text[offset + i] != cmp[i]) return false;
        }
        return true;
    }

    public void Expect(string match)
    {
        Clean();
        if (!StartsWith(match)) Fail("Bad input: expected '" + match + "'");
        offset += match.Length;
    }

    public bool Accept(string match)
    {
        Clean();
        if (!StartsWith(match)) return false;
        offset += match.Length;
        return true;
    }

    public bool GetLong(ref long l)
    {
        Clean();
        BeginTransaction();

        bool neg = false;

        if (Accept("-")) neg = true;
        if (offset == text.Length)
        {
            AbortTransaction();
            return false;
        }

        // special number "0" - only case where a number starting with 0 is acceptable
        if (text[offset] == '0')
        {
            l = 0;
            offset++;
        }
        else
        {
            // first digit: 1..9
            if (text[offset] < '1' || text[offset] > '9')
            {
                AbortTransaction();
                return false;
            }

            l = text[offset] - '0';
            offset++;

            while (offset < text.Length)
            {
                if (text[offset] < '0' || text[offset] > '9') break;

                int digit = text[offset] - '0';
                l = l * 10 + digit;
                offset++;
            }
        }
        if (neg) l = -l;

        CommitTransaction();
        return true;
    }

    public bool GetDouble(ref double d)
    {
        BeginTransaction();

        long intpart = 0;
        bool must_have_digit = false;
        bool negate = false;

        if (Accept("NaN"))
        {
            d = Double.NaN;
            CommitTransaction();
            return true;
        }

        if (Accept("-")) negate = true;

        if (GetLong(ref intpart))
        {
            d = intpart;
        }
        else
        {
            if (!Accept("."))
            {
                AbortTransaction();
                return false;
            }
            d = 0;
            must_have_digit = true; // "." is not a float.
        }

        double fraction = 0.1;

        if (!Accept("."))
        {
            CommitTransaction();
            if (negate) d = -d;
            return true;
        }

        bool has_digit = false;

        while (offset < text.Length)
        {
            if (text[offset] < '0' || text[offset] > '9') break;

            int digit = text[offset] - '0';
            d = d + fraction * digit;
            fraction = fraction * 0.1;
            has_digit = true;
            offset++;
        }

        if (must_have_digit && !has_digit)
        {
            AbortTransaction();
            return false;
        }

        CommitTransaction();
        if (negate) d = -d;
        return true;
    }
}