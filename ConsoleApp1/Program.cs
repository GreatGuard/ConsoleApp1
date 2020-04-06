using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace ConsoleApp1
{
    class Program
    {
        static void Main(string[] args)
        {
            //Console.WriteLine(typeof(int).Assembly.FullName);
            //Console.WriteLine(typeof(string).Assembly.FullName);
            //Console.WriteLine(typeof(bool).Assembly.FullName);
            //Console.WriteLine("Hello World!");
            //var root = new Cat()
            //    .Register<IFoo, Foo>(Lifetime.Transisent)
            //    .Register<IBar>(_ => new Bar(), Lifetime.Self)
            //    .Register<IBaz, Baz>(Lifetime.Root)
            //    .Register(Assembly.GetEntryAssembly());
            //var cat1 = root.CreateChild();
            //var cat2 = root.CreateChild();
            //void GetServices<TService>(Cat cat)
            //{
            //    cat.GetService<TService>();
            //    cat.GetService<TService>();
            //}
            //GetServices<IFoo>(cat1);
            //GetServices<IBar>(cat1);
            //GetServices<IBaz>(cat1);
            //GetServices<IQux>(cat1);
            //Console.WriteLine();
            //GetServices<IFoo>(cat2);
            //GetServices<IBar>(cat2);
            //GetServices<IBaz>(cat2);
            //GetServices<IQux>(cat2);

            var cat = new Cat()
                .Register<IFoo, Foo>(Lifetime.Transisent)
                .Register<IBar, Bar>(Lifetime.Transisent)
                .Register(typeof(IFoobar<,>), typeof(Foobar<,>), Lifetime.Transisent);
            var foobar = (Foobar<IFoo, IBar>)cat.GetService<IFoobar<IFoo, IBar>>();
            Debug.Assert(foobar.Foo is Foo);
            Debug.Assert(foobar.Bar is Bar);
        }
    }


    public enum Lifetime
    {
        Root,
        Self,
        Transisent
    }

    public interface IFoo { }
    public interface IBar { }
    public interface IBaz { }
    public interface IQux { }
    public interface IFoobar<T1, T2> { }
    public class Base : IDisposable
    {
        public Base() => Console.WriteLine($"Instance of {GetType().Name} is created.");
        public void Dispose() => Console.WriteLine($"Instance of {GetType().Name} is disposed.");
    }

    public class Foo : Base, IFoo { }
    public class Bar : Base, IBar { }
    public class Baz : Base, IBaz { }

    [MapTo(typeof(IQux), Lifetime.Root)]
    public class Qux : Base, IQux { }

    

    public class Foobar<T1, T2> : IFoobar<T1, T2>
    {
        public IFoo Foo { get; }
        public IBar Bar { get; }
        public Foobar(IFoo foo, IBar bar)
        {
            Foo = foo;
            Bar = bar;
        }
    }

    public class ServiceRegistry
    {
        public Type ServiceType { get; }
        public Lifetime Lifetime { get; }
        public Func<Cat, Type[], object> Factory { get; }
        internal ServiceRegistry Next { get; set; }
        public ServiceRegistry(Type serviceType, Lifetime lifetime, Func<Cat, Type[], object> factory)
        {
            ServiceType = serviceType;
            Lifetime = lifetime;
            Factory = factory;
        }
        internal IEnumerable<ServiceRegistry> AsEnumerable()
        {
            var list = new List<ServiceRegistry>();
            for (var self = this; self != null; self = self.Next)
            {
                list.Add(self);
            }
            return list;
        }
    }

    public class Cat : IServiceProvider, IDisposable
    {
        internal readonly Cat _root;
        internal readonly ConcurrentDictionary<Type, ServiceRegistry> _registries;
        private readonly ConcurrentDictionary<Key, object> _services;
        private readonly ConcurrentBag<IDisposable> _disposables;
        private volatile bool _disposed;

        public Cat()
        {
            _registries = new ConcurrentDictionary<Type, ServiceRegistry>();
            _root = this;
            _services = new ConcurrentDictionary<Key, object>();
            _disposables = new ConcurrentBag<IDisposable>();
        }

        internal Cat(Cat parent)
        {
            _root = parent._root;
            _registries = _root._registries;
            _services = new ConcurrentDictionary<Key, object>();
            _disposables = new ConcurrentBag<IDisposable>();
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("Cat");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
            _disposables.Clear();
            _services.Clear();
        }

        private object GetServiceCore(ServiceRegistry registry, Type[] genericArguments)
        {
            var key = new Key(registry, genericArguments);
            var serviceType = registry.ServiceType;
            switch (registry.Lifetime)
            {
                case Lifetime.Root:
                    return GetOrCreate(_root._services, _root._disposables);
                case Lifetime.Self:
                    return GetOrCreate(_services, _disposables);
                default:
                    {
                        var service = registry.Factory(this, genericArguments);
                        if (service is IDisposable disposable && disposable != this)
                        {
                            _disposables.Add(disposable);
                        }
                        return service;
                    }
            }

            object GetOrCreate(ConcurrentDictionary<Key, object> services, ConcurrentBag<IDisposable> disposables)
            {
                if (services.TryGetValue(key, out var service))
                {
                    return service;
                }
                service = registry.Factory(this, genericArguments);
                services[key] = service;
                if (service is IDisposable disposable)
                {
                    disposables.Add(disposable);
                }
                return service;
            }
        }

        public object GetService(Type serviceType)
        {
            EnsureNotDisposed();
            if (serviceType == typeof(Cat) || serviceType == typeof(IServiceProvider))
            {
                return this;
            }

            ServiceRegistry registry;
            //IEnumerable<T>
            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var elementType = serviceType.GetGenericArguments()[0];
                if (!_registries.TryGetValue(elementType, out registry))
                {
                    return Array.CreateInstance(elementType, 0);
                }
                var registries = registry.AsEnumerable();
                var services = registries.Select(it => GetServiceCore(it, Type.EmptyTypes)).ToArray();
                Array array = Array.CreateInstance(elementType, services.Length);
                services.CopyTo(array, 0);
                return array;
            }

            //Generic
            if (serviceType.IsGenericType && !_registries.ContainsKey(serviceType))
            {
                var definition = serviceType.GetGenericTypeDefinition();
                return _registries.TryGetValue(definition, out registry)
                    ? GetServiceCore(registry, serviceType.GetGenericArguments())
                    : null;
            }

            //normal
            return _registries.TryGetValue(serviceType, out registry)
                ? GetServiceCore(registry, new Type[0])
                : null;
        }

        public Cat Register(ServiceRegistry registry)
        {
            EnsureNotDisposed();
            if (_registries.TryGetValue(registry.ServiceType, out var existing))
            {
                _registries[registry.ServiceType] = registry;
                registry.Next = existing;
            }
            else
            {
                _registries[registry.ServiceType] = registry;
            }
            return this;
        }
    }

    internal class Key : IEquatable<Key>
    {
        public ServiceRegistry Registry { get; }
        public Type[] GenericArguments { get; }
        public Key(ServiceRegistry registry, Type[] genericAugumnets)
        {
            Registry = registry;
            GenericArguments = genericAugumnets;
        }
        public bool Equals(Key other)
        {
            if (Registry != other.Registry)
            {
                return false;
            }
            if (GenericArguments.Length != other.GenericArguments.Length)
            {
                return false;
            }
            for (int index = 0; index < GenericArguments.Length; index++)
            {
                if (GenericArguments[index] != other.GenericArguments[index])
                {
                    return false;
                }
            }
            return true;
        }

        public override int GetHashCode()
        {
            var hashCode = Registry.GetHashCode();
            for(int index = 0; index < GenericArguments.Length; index++)
            {
                hashCode ^= GenericArguments[index].GetHashCode();
            }
            return hashCode;
        }
        public override bool Equals(object obj)
        {
            return obj is Key key ? Equals(key) : false;
        }
    }

    public static class CatExtensions
    {
        public static Cat Register(this Cat cat, Type from, Type to, Lifetime lifetime)
        {
            Func<Cat, Type[], object> factory = (_, arguments) => Create(_, to, arguments);
            cat.Register(new ServiceRegistry(from, lifetime, factory));
            return cat;
        }
        public static Cat Register<TFrom, TTo>(this Cat cat, Lifetime lifetime)
            where TTo : TFrom => cat.Register(typeof(TFrom), typeof(TTo), lifetime);
        public static Cat Register(this Cat cat, Type serviceType, object instance)
        {
            Func<Cat, Type[], object> factory = (_, arguments) => instance;
            cat.Register(new ServiceRegistry(serviceType, Lifetime.Root, factory));
            return cat;
        }
        public static Cat Register<TService>(this Cat cat, TService instance)
        {
            Func<Cat, Type[], object> factory = (_, arguments) => instance;
            cat.Register(new ServiceRegistry(typeof(TService), Lifetime.Root, factory));
            return cat;
        }
        public static Cat Register(this Cat cat, Type serviceType, Func<Cat, object> factory, Lifetime lifetime)
        {
            cat.Register(new ServiceRegistry(serviceType, lifetime,
                (_, arguments) => factory(_)));
            return cat;
        }
        public static Cat Register<TService>(this Cat cat, Func<Cat, TService> factory, Lifetime lifetime)
        {
            cat.Register(new ServiceRegistry(typeof(TService), lifetime,
                (_, arguments) => factory(_)));
            return cat;
        }
        private static object Create(Cat cat, Type type, Type[] genericArguments)
        {
            if (genericArguments.Length > 0)
            {
                type = type.MakeGenericType(genericArguments);
            }
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
            {
                throw new InvalidOperationException($"cannot {type}.");
            }
            var constructor = constructors.FirstOrDefault(it => it.GetCustomAttributes(false).OfType<InjectionAttribute>().Any());
            constructor ??= constructors.First();
            var parameters = constructor.GetParameters();
            if (parameters.Length == 0)
            {
                return Activator.CreateInstance(type);
            }
            var arguments = new object[parameters.Length];
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = cat.GetService(parameters[index].ParameterType);
            }
            return constructor.Invoke(arguments);

        }
        public static Cat Register(this Cat cat, Assembly assembly)
        {
            var typedAttrubutes = from type in assembly.GetExportedTypes()
                                  let attribute = type.GetCustomAttribute<MapToAttribute>()
                                  where attribute != null
                                  select new { ServiceType = type, Attribute = attribute };
            foreach (var typedAttribue in typedAttrubutes)
            {
                cat.Register(typedAttribue.Attribute.ServiceType, typedAttribue.ServiceType, typedAttribue.Attribute.Lifetime);
            }
            return cat;
        }
        public static T GetService<T>(this Cat cat) => (T)cat.GetService(typeof(T));
        public static IEnumerable<T> GetServices<T>(this Cat cat) => cat.GetService<IEnumerable<T>>();
        public static Cat CreateChild(this Cat cat) => new Cat(cat);
    }

    [AttributeUsage(AttributeTargets.Constructor)]
    public class InjectionAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class MapToAttribute : Attribute
    {
        public Type ServiceType { get; }
        public Lifetime Lifetime { get; }
        public MapToAttribute(Type serviceType, Lifetime lifetime)
        {
            ServiceType = serviceType;
            Lifetime = lifetime;
        }
    }
}
