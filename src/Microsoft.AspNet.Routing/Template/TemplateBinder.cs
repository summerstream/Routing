// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNet.Http.Extensions;

namespace Microsoft.AspNet.Routing.Template
{
    public class TemplateBinder
    {
        private readonly RouteValueDictionary _defaults;
        private readonly RouteValueDictionary _filters;
        private readonly RouteTemplate _template;
        private readonly UrlEncoder _urlEncoder;

        public TemplateBinder(
            RouteTemplate template,
            UrlEncoder urlEncoder,
            RouteValueDictionary defaults)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            if (urlEncoder == null)
            {
                throw new ArgumentNullException(nameof(urlEncoder));
            }

            _template = template;
            _urlEncoder = urlEncoder;
            _defaults = defaults;

            // Any default that doesn't have a corresponding parameter is a 'filter' and if a value
            // is provided for that 'filter' it must match the value in defaults.
            _filters = new RouteValueDictionary(_defaults);
            foreach (var parameter in _template.Parameters)
            {
                _filters.Remove(parameter.Name);
            }
        }

        // Step 1: Get the list of values we're going to try to use to match and generate this URI
        public TemplateValuesResult GetValues(RouteValueDictionary ambientValues, RouteValueDictionary values)
        {
            var context = new TemplateBindingContext(_defaults);

            // Find out which entries in the URI are valid for the URI we want to generate.
            // If the URI had ordered parameters a="1", b="2", c="3" and the new values
            // specified that b="9", then we need to invalidate everything after it. The new
            // values should then be a="1", b="9", c=<no value>.
            for (var i = 0; i < _template.Parameters.Count; i++)
            {
                var parameter = _template.Parameters[i];

                // If it's a parameter subsegment, examine the current value to see if it matches the new value
                var parameterName = parameter.Name;

                object newParameterValue;
                var hasNewParameterValue = values.TryGetValue(parameterName, out newParameterValue);

                object currentParameterValue = null;
                var hasCurrentParameterValue = ambientValues != null &&
                                               ambientValues.TryGetValue(parameterName, out currentParameterValue);

                if (hasNewParameterValue && hasCurrentParameterValue)
                {
                    if (!RoutePartsEqual(currentParameterValue, newParameterValue))
                    {
                        // Stop copying current values when we find one that doesn't match
                        break;
                    }
                }

                // If the parameter is a match, add it to the list of values we will use for URI generation
                if (hasNewParameterValue)
                {
                    if (IsRoutePartNonEmpty(newParameterValue))
                    {
                        context.Accept(parameterName, newParameterValue);
                    }
                }
                else
                {
                    if (hasCurrentParameterValue)
                    {
                        context.Accept(parameterName, currentParameterValue);
                    }
                }
            }

            // Add all remaining new values to the list of values we will use for URI generation
            foreach (var kvp in values)
            {
                if (IsRoutePartNonEmpty(kvp.Value))
                {
                    context.Accept(kvp.Key, kvp.Value);
                }
            }

            // Accept all remaining default values if they match a required parameter
            for (var i = 0; i < _template.Parameters.Count; i++)
            {
                var parameter = _template.Parameters[i];
                if (parameter.IsOptional || parameter.IsCatchAll)
                {
                    continue;
                }

                if (context.NeedsValue(parameter.Name))
                {
                    // Add the default value only if there isn't already a new value for it and
                    // only if it actually has a default value, which we determine based on whether
                    // the parameter value is required.
                    context.AcceptDefault(parameter.Name);
                }
            }

            // Validate that all required parameters have a value.
            for (var i = 0; i < _template.Parameters.Count; i++)
            {
                var parameter = _template.Parameters[i];
                if (parameter.IsOptional || parameter.IsCatchAll)
                {
                    continue;
                }

                if (!context.AcceptedValues.ContainsKey(parameter.Name))
                {
                    // We don't have a value for this parameter, so we can't generate a url.
                    return null;
                }
            }

            // Any default values that don't appear as parameters are treated like filters. Any new values
            // provided must match these defaults.
            foreach (var filter in _filters)
            {
                var parameter = GetParameter(filter.Key);
                if (parameter != null)
                {
                    continue;
                }

                object value;
                if (values.TryGetValue(filter.Key, out value))
                {
                    if (!RoutePartsEqual(value, filter.Value))
                    {
                        // If there is a non-parameterized value in the route and there is a
                        // new value for it and it doesn't match, this route won't match.
                        return null;
                    }
                }
            }

            // Add any ambient values that don't match parameters - they need to be visible to constraints
            // but they will ignored by link generation.
            var combinedValues = new RouteValueDictionary(context.AcceptedValues);
            if (ambientValues != null)
            {
                foreach (var kvp in ambientValues)
                {
                    if (IsRoutePartNonEmpty(kvp.Value))
                    {
                        var parameter = GetParameter(kvp.Key);
                        if (parameter == null && !context.AcceptedValues.ContainsKey(kvp.Key))
                        {
                            combinedValues.Add(kvp.Key, kvp.Value);
                        }
                    }
                }
            }

            return new TemplateValuesResult()
            {
                AcceptedValues = context.AcceptedValues,
                CombinedValues = combinedValues,
            };
        }

        // Step 2: If the route is a match generate the appropriate URI
        public string BindValues(RouteValueDictionary acceptedValues)
        {
            var context = new UriBuildingContext(_urlEncoder);

            for (var i = 0; i < _template.Segments.Count; i++)
            {
                Debug.Assert(context.BufferState == SegmentState.Beginning);
                Debug.Assert(context.UriState == SegmentState.Beginning);

                var segment = _template.Segments[i];

                for (var j = 0; j < segment.Parts.Count; j++)
                {
                    var part = segment.Parts[j];

                    if (part.IsLiteral)
                    {
                        if (!context.Accept(part.Text))
                        {
                            return null;
                        }
                    }
                    else if (part.IsParameter)
                    {
                        // If it's a parameter, get its value
                        object value;
                        var hasValue = acceptedValues.TryGetValue(part.Name, out value);
                        if (hasValue)
                        {
                            acceptedValues.Remove(part.Name);
                        }

                        var isSameAsDefault = false;
                        object defaultValue;
                        if (_defaults != null && _defaults.TryGetValue(part.Name, out defaultValue))
                        {
                            if (RoutePartsEqual(value, defaultValue))
                            {
                                isSameAsDefault = true;
                            }
                        }

                        var converted = Convert.ToString(value, CultureInfo.InvariantCulture);
                        if (isSameAsDefault)
                        {
                            // If the accepted value is the same as the default value buffer it since
                            // we won't necessarily add it to the URI we generate.
                            if (!context.Buffer(converted))
                            {
                                return null;
                            }
                        }
                        else
                        {
                            // If the value is not accepted, it is null or empty value in the 
                            // middle of the segment. We accept this if the parameter is an
                            // optional parameter and it is preceded by an optional seperator.
                            // I this case, we need to remove the optional seperator that we
                            // have added to the URI
                            // Example: template = {id}.{format?}. parameters: id=5
                            // In this case after we have generated "5.", we wont find any value 
                            // for format, so we remove '.' and generate 5.
                            if (!context.Accept(converted))
                            {
                                if (j != 0 && part.IsOptional && segment.Parts[j - 1].IsOptionalSeperator)
                                {
                                    context.Remove(segment.Parts[j - 1].Text);
                                }
                                else
                                {
                                    return null;
                                }
                            }
                        }
                    }
                }

                context.EndSegment();
            }

            // Generate the query string from the remaining values
            var queryBuilder = new QueryBuilder();
            foreach (var kvp in acceptedValues)
            {
                if (_defaults != null && _defaults.ContainsKey(kvp.Key))
                {
                    // This value is a 'filter' we don't need to put it in the query string.
                    continue;
                }

                var converted = Convert.ToString(kvp.Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(converted))
                {
                    continue;
                }

                queryBuilder.Add(kvp.Key, converted);
            }

            var uri = context.GetUri();
            uri.Append(queryBuilder);
            return uri.ToString();
        }

        private TemplatePart GetParameter(string name)
        {
            for (var i = 0; i < _template.Parameters.Count; i++)
            {
                var parameter = _template.Parameters[i];
                if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return parameter;
                }
            }

            return null;
        }

        /// <summary>
        /// Compares two objects for equality as parts of a case-insensitive path.
        /// </summary>
        /// <param name="a">An object to compare.</param>
        /// <param name="b">An object to compare.</param>
        /// <returns>True if the object are equal, otherwise false.</returns>
        public static bool RoutePartsEqual(object a, object b)
        {
            var sa = a as string;
            var sb = b as string;

            if (sa != null && sb != null)
            {
                // For strings do a case-insensitive comparison
                return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                if (a != null && b != null)
                {
                    // Explicitly call .Equals() in case it is overridden in the type
                    return a.Equals(b);
                }
                else
                {
                    // At least one of them is null. Return true if they both are
                    return a == b;
                }
            }
        }

        private static bool IsRoutePartNonEmpty(object routePart)
        {
            var routePartString = routePart as string;
            if (routePartString == null)
            {
                return routePart != null;
            }
            else
            {
                return routePartString.Length > 0;
            }
        }

        [DebuggerDisplay("{DebuggerToString(),nq}")]
        private class TemplateBindingContext
        {
            private readonly RouteValueDictionary _defaults;
            private readonly RouteValueDictionary _acceptedValues;

            public TemplateBindingContext(RouteValueDictionary defaults)
            {
                _defaults = defaults;

                _acceptedValues = new RouteValueDictionary();
            }

            public RouteValueDictionary AcceptedValues
            {
                get { return _acceptedValues; }
            }

            public void Accept(string key, object value)
            {
                if (!_acceptedValues.ContainsKey(key))
                {
                    _acceptedValues.Add(key, value);
                }
            }

            public void AcceptDefault(string key)
            {
                Debug.Assert(!_acceptedValues.ContainsKey(key));

                object value;
                if (_defaults != null && _defaults.TryGetValue(key, out value))
                {
                    _acceptedValues.Add(key, value);
                }
            }

            public bool NeedsValue(string key)
            {
                return !_acceptedValues.ContainsKey(key);
            }

            private string DebuggerToString()
            {
                return string.Format("{{Accepted: '{0}'}}", string.Join(", ", _acceptedValues.Keys));
            }
        }

        [DebuggerDisplay("{DebuggerToString(),nq}")]
        private class UriBuildingContext
        {
            // Holds the 'accepted' parts of the uri.
            private readonly StringBuilder _uri;

            // Holds the 'optional' parts of the uri. We need a secondary buffer to handle cases where an optional
            // segment is in the middle of the uri. We don't know if we need to write it out - if it's
            // followed by other optional segments than we will just throw it away.
            private readonly List<BufferValue> _buffer;
            private readonly UrlEncoder _urlEncoder;
            private readonly StringWriter _uriWriter;

            private bool _hasEmptySegment;
            private int _lastValueOffset;

            public UriBuildingContext(UrlEncoder urlEncoder)
            {
                _urlEncoder = urlEncoder;
                _uri = new StringBuilder();
                _buffer = new List<BufferValue>();
                _uriWriter = new StringWriter(_uri);
                _lastValueOffset = -1;

                BufferState = SegmentState.Beginning;
                UriState = SegmentState.Beginning;
            }

            public SegmentState BufferState { get; private set; }

            public SegmentState UriState { get; private set; }

            public bool Accept(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    if (UriState == SegmentState.Inside || BufferState == SegmentState.Inside)
                    {
                        // We can't write an 'empty' part inside a segment
                        return false;
                    }
                    else
                    {
                        _hasEmptySegment = true;
                        return true;
                    }
                }
                else if (_hasEmptySegment)
                {
                    // We're trying to write text after an empty segment - this is not allowed.
                    return false;
                }

                for (var i = 0; i < _buffer.Count; i++)
                {
                    if (_buffer[i].RequiresEncoding)
                    {
                        _urlEncoder.Encode(_uriWriter, _buffer[i].Value);
                    }
                    else
                    {
                        _uri.Append(_buffer[i].Value);
                    }
                }
                _buffer.Clear();

                if (UriState == SegmentState.Beginning && BufferState == SegmentState.Beginning)
                {
                    if (_uri.Length != 0)
                    {
                        _uri.Append("/");
                    }
                }

                BufferState = SegmentState.Inside;
                UriState = SegmentState.Inside;

                _lastValueOffset = _uri.Length;
                // Allow the first segment to have a leading slash.
                // This prevents the leading slash from PathString segments from being encoded.
                if (_uri.Length == 0 && value.Length > 0 && value[0] == '/')
                {
                    _uri.Append("/");
                    _urlEncoder.Encode(_uriWriter, value, 1, value.Length - 1);
                }
                else
                {
                    _urlEncoder.Encode(_uriWriter, value);
                }

                return true;
            }

            public void Remove(string literal)
            {
                Debug.Assert(_lastValueOffset != -1, "Cannot invoke Remove more than once.");
                _uri.Length = _lastValueOffset;
                _lastValueOffset = -1;
            }

            public bool Buffer(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    if (BufferState == SegmentState.Inside)
                    {
                        // We can't write an 'empty' part inside a segment
                        return false;
                    }
                    else
                    {
                        _hasEmptySegment = true;
                        return true;
                    }
                }
                else if (_hasEmptySegment)
                {
                    // We're trying to write text after an empty segment - this is not allowed.
                    return false;
                }

                if (UriState == SegmentState.Inside)
                {
                    // We've already written part of this segment so there's no point in buffering, we need to
                    // write out the rest or give up.
                    var result = Accept(value);

                    // We've already checked the conditions that could result in a rejected part, so this should
                    // always be true.
                    Debug.Assert(result);

                    return result;
                }

                if (UriState == SegmentState.Beginning && BufferState == SegmentState.Beginning)
                {
                    if (_uri.Length != 0 || _buffer.Count != 0)
                    {
                        _buffer.Add(new BufferValue("/", requiresEncoding: false));
                    }

                    BufferState = SegmentState.Inside;
                }

                _buffer.Add(new BufferValue(value, requiresEncoding: true));
                return true;
            }

            internal void EndSegment()
            {
                BufferState = SegmentState.Beginning;
                UriState = SegmentState.Beginning;
            }

            internal StringBuilder GetUri()
            {
                // We can ignore any currently buffered segments - they are are guaranteed to be 'defaults'.
                return _uri;
            }

            private string DebuggerToString()
            {
                return string.Format("{{Accepted: '{0}' Buffered: '{1}'}}", _uri, string.Join("", _buffer));
            }
        }

        // Segments are treated as all-or-none. We should never output a partial segment.
        // If we add any subsegment of this segment to the generated URI, we have to add
        // the complete match. For example, if the subsegment is "{p1}-{p2}.xml" and we
        // used a value for {p1}, we have to output the entire segment up to the next "/".
        // Otherwise we could end up with the partial segment "v1" instead of the entire
        // segment "v1-v2.xml".
        private enum SegmentState
        {
            Beginning,
            Inside,
        }

        private struct BufferValue
        {
            public BufferValue(string value, bool requiresEncoding)
            {
                Value = value;
                RequiresEncoding = requiresEncoding;
            }

            public bool RequiresEncoding { get; }

            public string Value { get; }
        }
    }
}
