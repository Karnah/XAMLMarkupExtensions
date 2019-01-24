﻿#region Copyright information
// <copyright file="NestedMarkupExtension.cs">
//     Licensed under Microsoft Public License (Ms-PL)
//     http://xamlmarkupextensions.codeplex.com/license
// </copyright>
// <author>Uwe Mayer</author>
#endregion

namespace XAMLMarkupExtensions.Base
{
    #region Uses
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Windows;
    using System.Windows.Markup;
#if !NET35
    using System.Windows.Controls;
    using System.Xaml;
#endif
    #endregion

    /// <summary>
    /// This class walks up the tree of markup extensions to support nesting.
    /// Based on <see href="https://github.com/SeriousM/WPFLocalizationExtension"/>
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public abstract class NestedMarkupExtension : MarkupExtension, INestedMarkupExtension, IDisposable
    {
        /// <summary>
        /// Holds the collection of assigned dependency objects as WeakReferences
        /// Instead of a single reference, a list is used, if this extension is applied to multiple instances.
        ///
        /// The values are lists of tuples, containing the target property and property type.
        /// </summary>
        private readonly Dictionary<WeakReference, Dictionary<Tuple<object, int>, Type>> targetObjects = new Dictionary<WeakReference, Dictionary<Tuple<object, int>, Type>>();

        /// <summary>
        /// Holds the markup extensions root object hash code.
        /// </summary>
        private int rootObjectHashCode;

        /// <summary>
        /// Get the target objects and properties.
        /// </summary>
        /// <returns>A list of target objects.</returns>
        private List<TargetInfo> GetTargetObjectsAndProperties()
        {
            List<TargetInfo> list = new List<TargetInfo>();

            // Select all targets that are still alive.
            foreach (var target in targetObjects)
            {
                var targetReference = target.Key.Target;
                if (targetReference == null)
                    continue;

                list.AddRange(from kvp in target.Value
                              select new TargetInfo(targetReference, kvp.Key.Item1, kvp.Value, kvp.Key.Item2));
            }

            return list;
        }

        /// <summary>
        /// Get the paths to all target properties through the nesting hierarchy.
        /// </summary>
        /// <returns>A list of paths to the properties.</returns>
        public List<TargetPath> GetTargetPropertyPaths()
        {
            var list = new List<TargetPath>();
            var objList = GetTargetObjectsAndProperties();

            foreach (var info in objList)
            {
                if (info.IsEndpoint)
                {
                    TargetPath path = new TargetPath(info);
                    list.Add(path);
                }
                else
                {
                    foreach (var path in ((INestedMarkupExtension)info.TargetObject).GetTargetPropertyPaths())
                    {
                        // Push the ITargetMarkupExtension
                        path.AddStep(info);
                        // Add the tuple to the list
                        list.Add(path);
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// An action that is called when the first target is bound.
        /// </summary>
        protected Action OnFirstTarget;

        /// <summary>
        /// This function must be implemented by all child classes.
        /// It shall return the properly prepared output of the markup extension.
        /// </summary>
        /// <param name="info">Information about the target.</param>
        /// <param name="endPoint">Information about the endpoint.</param>
        public abstract object FormatOutput(TargetInfo endPoint, TargetInfo info);

        /// <summary>
        /// Check, if the given target is connected to this markup extension.
        /// </summary>
        /// <param name="info">Information about the target.</param>
        /// <returns>True, if a connection exits.</returns>
        public bool IsConnected(TargetInfo info)
        {
            WeakReference wr = (from kvp in targetObjects
                                where kvp.Key.Target == info.TargetObject
                                select kvp.Key).FirstOrDefault();

            if (wr == null)
                return false;

            Tuple<object, int> tuple = new Tuple<object, int>(info.TargetProperty, info.TargetPropertyIndex);

            return targetObjects[wr].ContainsKey(tuple);
        }

        /// <summary>
        /// Override this function, if (and only if) additional information is needed from the <see cref="IServiceProvider"/> instance that is passed to <see cref="NestedMarkupExtension.ProvideValue"/>.
        /// </summary>
        /// <param name="serviceProvider">A service provider.</param>
        protected virtual void OnServiceProviderChanged(IServiceProvider serviceProvider)
        {
            // Do nothing in the base class.
        }

        /// <summary>
        /// The ProvideValue method of the <see cref="MarkupExtension"/> base class.
        /// </summary>
        /// <param name="serviceProvider">A service provider.</param>
        /// <returns>The value of the extension, or this if something gone wrong (needed for Templates).</returns>
        public sealed override object ProvideValue(IServiceProvider serviceProvider)
        {
            // If the service provider is null, return this
            if (serviceProvider == null)
                return this;

            OnServiceProviderChanged(serviceProvider);

            // Try to cast the passed serviceProvider to a IProvideValueTarget
            IProvideValueTarget service = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

            // If the cast fails, return this
            if (service == null)
                return this;

#if NET35
            rootObjectHashCode = 0;
#else
            // Try to cast the passed serviceProvider to a IRootObjectProvider and if the cast fails return null
            IRootObjectProvider rootObject = serviceProvider.GetService(typeof(IRootObjectProvider)) as IRootObjectProvider;
            if (rootObject == null)
            {
                rootObjectHashCode = 0;
            }
            else
            {
                rootObjectHashCode = rootObject.RootObject.GetHashCode();

                // We only sign up once to the Window Closed event to clear the listeners list of root object.
                if (rootObject.RootObject != null && !EndpointReachedEvent.ContainsRootObjectHash(rootObjectHashCode))
                {
                    if (rootObject.RootObject is Window window)
                    {
                        window.Closed += delegate (object sender, EventArgs args) { EndpointReachedEvent.ClearListenersForRootObject(rootObjectHashCode); };
                    }
                    else if (rootObject.RootObject is FrameworkElement frameworkElement)
                    {
                        void frameworkElementUnloadedHandler(object sender, RoutedEventArgs args)
                        {
                            frameworkElement.Unloaded -= frameworkElementUnloadedHandler;
                            EndpointReachedEvent.ClearListenersForRootObject(rootObjectHashCode);
                        }

                        frameworkElement.Unloaded += frameworkElementUnloadedHandler;
                    }
                }
            }
#endif

            // Declare a target object and property
            TargetInfo endPoint = null;
            object targetObject = service.TargetObject;
            object targetProperty = service.TargetProperty;
            int targetPropertyIndex = -1;
            Type targetPropertyType = null;

            // First, check if the service provider is of type SimpleProvideValueServiceProvider
            //      -> If yes, get the target property type and index.
            // Check if the service.TargetProperty is a DependencyProperty or a PropertyInfo and set the type info
            if (serviceProvider is SimpleProvideValueServiceProvider)
            {
                targetPropertyType = ((SimpleProvideValueServiceProvider)serviceProvider).TargetPropertyType;
                targetPropertyIndex = ((SimpleProvideValueServiceProvider)serviceProvider).TargetPropertyIndex;
                endPoint = ((SimpleProvideValueServiceProvider)serviceProvider).EndPoint;
            }
            else
            {
                if (targetProperty is PropertyInfo)
                {
                    PropertyInfo pi = (PropertyInfo)targetProperty;
                    targetPropertyType = pi.PropertyType;

                    // Kick out indexers.
                    if (pi.GetIndexParameters().Any())
                        throw new InvalidOperationException("Indexers are not supported!");
                }
                else if (targetProperty is DependencyProperty)
                {
                    DependencyProperty dp = (DependencyProperty)targetProperty;
                    targetPropertyType = dp.PropertyType;
                }
                else
                    return this;
            }

            // If the service.TargetObject is System.Windows.SharedDp (= not a DependencyObject and not a PropertyInfo), we return "this".
            // The SharedDp will call this instance later again.
            if (!(targetObject is DependencyObject) && !(targetProperty is PropertyInfo))
                return this;

            // If the target object is a DictionaryEntry we presumably are facing a resource scenario.
            // We will be called again later with the proper target.
            if (targetObject is DictionaryEntry)
                return null;

            // Search for the target in the target object list
            WeakReference wr = (from kvp in targetObjects
                                where kvp.Key.Target == targetObject
                                select kvp.Key).FirstOrDefault();

            if (wr == null)
            {
                // If it's the first object, call the appropriate action
                if (targetObjects.Count == 0)
                {
                    if (OnFirstTarget != null)
                        OnFirstTarget();
                }

                // Add the target as a WeakReference to the target object list
                wr = new WeakReference(targetObject);
                targetObjects.Add(wr, new Dictionary<Tuple<object, int>, Type>());

                // Add this extension to the ObjectDependencyManager to ensure the lifetime along with the target object
                ObjectDependencyManager.AddObjectDependency(wr, this);
            }

            // Finally, add the target prop and info to the list of this WeakReference
            Tuple<object, int> tuple = new Tuple<object, int>(targetProperty, targetPropertyIndex);
            if (!targetObjects[wr].ContainsKey(tuple))
                targetObjects[wr].Add(tuple, targetPropertyType);

            // Sign up to the EndpointReachedEvent only if the markup extension wants to do so.
            EndpointReachedEvent.AddListener(rootObjectHashCode, this);

            // Create the target info
            TargetInfo info = new TargetInfo(targetObject, targetProperty, targetPropertyType, targetPropertyIndex);

            // Return the result of FormatOutput
            object result = null;

            if (info.IsEndpoint)
            {
                var args = new EndpointReachedEventArgs(info);
                EndpointReachedEvent.Invoke(rootObjectHashCode, this, args);
                result = args.EndpointValue;
            }
            else
                result = FormatOutput(endPoint, info);

            // Check type
            if (typeof(IList).IsAssignableFrom(targetPropertyType))
                return result;
            else if ((result != null) && targetPropertyType.IsAssignableFrom(result.GetType()))
                return result;

            // Finally, if nothing was there, return null or default
            if (targetPropertyType.IsValueType)
                return Activator.CreateInstance(targetPropertyType);
            else
                return null;
        }

        /// <summary>
        /// Set the new value for all targets.
        /// </summary>
        protected void UpdateNewValue()
        {
            UpdateNewValue(null);
        }

        /// <summary>
        /// Trigger the update of the target(s).
        /// </summary>
        /// <param name="targetPath">A specific path to follow or null for all targets.</param>
        /// <returns>The output of the path at the endpoint.</returns>
        public object UpdateNewValue(TargetPath targetPath)
        {
            if (targetPath == null)
            {
                // No path supplied - send it to all targets.
                foreach (var path in GetTargetPropertyPaths())
                {
                    // Call yourself and supply the path to follow.
                    UpdateNewValue(path);
                }
            }
            else
            {
                // Get the info of the next step.
                TargetInfo info = targetPath.GetNextStep();

                // Get the own formatted output.
                object output = FormatOutput(targetPath.EndPoint, info);

                var target = targetPath.EndPoint.TargetObject as DependencyObject;
                if (target == null ||
                    !target.IsSealed)
                {
                    // Set the property of the target to the new value.
                    SetPropertyValue(output, info, false);
                }

                // Have we reached the endpoint?
                // If not, call the UpdateNewValue function of the next ITargetMarkupExtension
                if (info.IsEndpoint)
                    return output;
                else
                    return ((INestedMarkupExtension)info.TargetObject).UpdateNewValue(targetPath);
            }

            return null;
        }

        /// <summary>
        /// Sets the value of a property of type PropertyInfo or DependencyProperty.
        /// </summary>
        /// <param name="value">The new value.</param>
        /// <param name="info">The target information.</param>
        /// <param name="forceNull">Determines, whether null values should be written.</param>
        public static void SetPropertyValue(object value, TargetInfo info, bool forceNull)
        {
            if ((value == null) && !forceNull)
                return;

            // Anyway, a value type cannot receive null values...
            if (info.TargetPropertyType.IsValueType && (value == null))
                value = Activator.CreateInstance(info.TargetPropertyType);

            // Set the value.
            if (info.TargetProperty is DependencyProperty)
                ((DependencyObject)info.TargetObject).SetValueSync((DependencyProperty)info.TargetProperty, value);
            else
            {
                PropertyInfo pi = (PropertyInfo)info.TargetProperty;

                if (typeof(IList).IsAssignableFrom(info.TargetPropertyType) && (value != null) && !info.TargetPropertyType.IsAssignableFrom(value.GetType()))
                {
                    // A list, a list - get it and set the value directly via its index.
                    if (info.TargetPropertyIndex >= 0)
                    {
                        IList list = (IList)pi.GetValue(info.TargetObject, null);
                        if (list.Count > info.TargetPropertyIndex)
                            list[info.TargetPropertyIndex] = value;
                    }
                    return;
                }

                var target = info.TargetObject as DependencyObject;
                if (target == null ||
                    !target.IsSealed)
                {
                    pi.SetValue(info.TargetObject, value, null);
                }
            }
        }

        /// <summary>
        /// Gets the value of a property of type PropertyInfo or DependencyProperty.
        /// </summary>
        /// <param name="info">The target information.</param>
        /// <returns>The value.</returns>
        public static object GetPropertyValue(TargetInfo info)
        {
            if (info.TargetProperty is DependencyProperty)
                return ((DependencyObject)info.TargetObject).GetValueSync<object>((DependencyProperty)info.TargetProperty);
            else if (info.TargetProperty is PropertyInfo)
            {
                PropertyInfo pi = (PropertyInfo)info.TargetProperty;

                if (info.TargetPropertyIndex >= 0)
                {
                    if (typeof(IList).IsAssignableFrom(info.TargetPropertyType))
                    {
                        IList list = (IList)pi.GetValue(info.TargetObject, null);
                        if (list.Count > info.TargetPropertyIndex)
                            return list[info.TargetPropertyIndex];
                    }
                }

                return ((PropertyInfo)info.TargetProperty).GetValue(info.TargetObject, null);
            }

            return null;
        }

        /// <summary>
        /// Safely get the value of a property that might be set by a further MarkupExtension.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="value">The value supplied by the set accessor of the property.</param>
        /// <param name="property">The property information.</param>
        /// <param name="index">The index of the indexed property, if applicable.</param>
        /// <returns>The value or default.</returns>
        protected T GetValue<T>(object value, PropertyInfo property, int index)
        {
            return GetValue<T>(value, property, index, null);
        }

        /// <summary>
        /// Safely get the value of a property that might be set by a further MarkupExtension.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="value">The value supplied by the set accessor of the property.</param>
        /// <param name="property">The property information.</param>
        /// <param name="index">The index of the indexed property, if applicable.</param>
        /// <param name="endPoint">An optional endpoint information.</param>
        /// <returns>The value or default.</returns>
        protected T GetValue<T>(object value, PropertyInfo property, int index, TargetInfo endPoint)
        {
            // Simple case: value is of same type
            if (value is T)
                return (T)value;

            // No property supplied
            if (property == null)
                return default(T);

            // Is value of type MarkupExtension?
            if (value is MarkupExtension)
            {
                object result = ((MarkupExtension)value).ProvideValue(new SimpleProvideValueServiceProvider(this, property, property.PropertyType, index, endPoint));
                if (result != null)
                    return (T)result;
                else
                    return default(T);
            }

            // Default return path.
            return default(T);
        }

        /// <summary>
        /// This method must return true, if an update shall be executed when the given endpoint is reached.
        /// This method is called each time an endpoint is reached.
        /// </summary>
        /// <param name="endpoint">Information on the specific endpoint.</param>
        /// <returns>True, if an update of the path to this endpoint shall be performed.</returns>
        protected abstract bool UpdateOnEndpoint(TargetInfo endpoint);

        /// <summary>
        /// Get the path to a specific endpoint.
        /// </summary>
        /// <param name="endpoint">The endpoint info.</param>
        /// <returns>The path to the endpoint.</returns>
        protected TargetPath GetPathToEndpoint(TargetInfo endpoint)
        {
            return (from p in GetTargetPropertyPaths() where p.EndPoint.Equals(endpoint) select p).FirstOrDefault();
        }

        /// <summary>
        /// Checks the existance of the given object in the target endpoint list.
        /// </summary>
        /// <param name="obj">The object.</param>
        /// <returns>True, if the extension nesting tree reaches the given object.</returns>
        protected bool IsEndpointObject(object obj)
        {
            return (from p in GetTargetPropertyPaths() where p.EndPoint.TargetObject == obj select p).Count() > 0;
        }

        /// <summary>
        /// An event handler that is called from the static <see cref="EndpointReachedEvent"/> class.
        /// </summary>
        /// <param name="sender">The markup extension that reached an enpoint.</param>
        /// <param name="args">The event args containing the endpoint information.</param>
        private void OnEndpointReached(NestedMarkupExtension sender, EndpointReachedEventArgs args)
        {
            if (args.Handled)
                return;

            var path = GetPathToEndpoint(args.Endpoint);

            if (path == null)
                return;

            if ((this != sender) && !UpdateOnEndpoint(path.EndPoint))
                return;

            args.EndpointValue = UpdateNewValue(path);

            // Removed, because of no use:
            // args.Handled = true;
        }

        /// <summary>
        /// Implements the IDisposable.Dispose function.
        /// </summary>
        public void Dispose()
        {
            EndpointReachedEvent.RemoveListener(rootObjectHashCode, this);
            targetObjects.Clear();
        }

        #region EndpointReachedEvent
        /// <summary>
        /// A static proxy class that handles endpoint reached events for a list of weak references of <see cref="NestedMarkupExtension"/>.
        /// This circumvents the usage of a WeakEventManager while providing a static instance that is capable of firing the event.
        /// </summary>
        internal static class EndpointReachedEvent
        {
            /// <summary>
            /// A dicitonary which contains a list of listeners per unique rootObject hash.
            /// </summary>
            private static readonly Dictionary<int, List<WeakReference>> listeners;
            private static readonly object listenersLock;

            /// <summary>
            /// Fire the event.
            /// </summary>
            /// <param name="rootObjectHashCode"><paramref name="sender"/>s root object hash code.</param>
            /// <param name="sender">The markup extension that reached an end point.</param>
            /// <param name="args">The event args containing the endpoint information.</param>
            internal static void Invoke(int rootObjectHashCode, NestedMarkupExtension sender, EndpointReachedEventArgs args)
            {
                lock (listenersLock)
                {
                    // Do nothing if we don't have this root object hash.
                    if (!listeners.ContainsKey(rootObjectHashCode))
                        return;

                    foreach (var wr in listeners[rootObjectHashCode].ToList())
                    {
                        var targetReference = wr.Target;
                        if (targetReference is NestedMarkupExtension)
                            ((NestedMarkupExtension)targetReference).OnEndpointReached(sender, args);
                        else
                            listeners[rootObjectHashCode].Remove(wr);
                    }
                }
            }

            /// <summary>
            /// Adds a listener to the inner list of listeners.
            /// </summary>
            /// <param name="rootObjectHashCode"><paramref name="listener"/>s root object hash code.</param>
            /// <param name="listener">The listener to add.</param>
            internal static void AddListener(int rootObjectHashCode, NestedMarkupExtension listener)
            {
                if (listener == null)
                    return;

                lock (listenersLock)
                {
                    // Do we have a listeners list for this root object yet, if not add it.
                    if (!listeners.ContainsKey(rootObjectHashCode))
                    {
                        listeners[rootObjectHashCode] = new List<WeakReference>();
                    }

                    // Check, if this listener already was added.
                    foreach (var wr in listeners[rootObjectHashCode].ToList())
                    {
                        var targetReference = wr.Target;
                        if (targetReference == null)
                            listeners[rootObjectHashCode].Remove(wr);
                        else if (targetReference == listener)
                            return;
                        else
                        {
                            var existing = (NestedMarkupExtension)targetReference;
                            var targets = existing.GetTargetObjectsAndProperties();

                            foreach (var target in targets)
                            {
                                if (listener.IsConnected(target))
                                {
                                    listeners[rootObjectHashCode].Remove(wr);
                                    break;
                                }
                            }
                        }
                    }

                    // Add it now.
                    listeners[rootObjectHashCode].Add(new WeakReference(listener));
                }
            }

#if !NET35
            /// <summary>
            /// Clears the listeners list for the given root object hash code <paramref name="rootObjectHashCode"/>.
            /// </summary>
            /// <param name="rootObjectHashCode"></param>
            internal static void ClearListenersForRootObject(int rootObjectHashCode)
            {
                lock (listenersLock)
                {
                    if (!listeners.ContainsKey(rootObjectHashCode))
                        return;

                    listeners[rootObjectHashCode].Clear();
                    listeners.Remove(rootObjectHashCode);
                }
            }

            /// <summary>
            /// Returns true if the given <paramref name="rootObjectHashCode"/> is already added, false otherwise.
            /// </summary>
            /// <param name="rootObjectHashCode">Root object hash code to check.</param>
            /// <returns>Returns true if the given <paramref name="rootObjectHashCode"/> is already added, false otherwise.</returns>
            internal static bool ContainsRootObjectHash(int rootObjectHashCode)
            {
                return listeners.ContainsKey(rootObjectHashCode);
            }
#endif

            /// <summary>
            /// Removes a listener from the inner list of listeners.
            /// </summary>
            /// <param name="rootObjectHashCode"><paramref name="listener"/>s root object hash code.</param>
            /// <param name="listener">The listener to remove.</param>
            internal static void RemoveListener(int rootObjectHashCode, NestedMarkupExtension listener)
            {
                if (listener == null)
                    return;

                lock (listenersLock)
                {
                    if (!listeners.ContainsKey(rootObjectHashCode))
                        return;

                    foreach (var wr in listeners[rootObjectHashCode].ToList())
                    {
                        var targetReference = wr.Target;
                        if (targetReference == null)
                            listeners[rootObjectHashCode].Remove(wr);
                        else if ((NestedMarkupExtension)targetReference == listener)
                            listeners[rootObjectHashCode].Remove(wr);
                    }

                    if (listeners[rootObjectHashCode].Count == 0)
                        listeners.Remove(rootObjectHashCode);
                }
            }

            /// <summary>
            /// An empty static constructor to prevent the class from being marked as beforefieldinit.
            /// </summary>
            static EndpointReachedEvent()
            {
                listeners = new Dictionary<int, List<WeakReference>>();
                listenersLock = new object();
            }
        }
        #endregion
    }
}
