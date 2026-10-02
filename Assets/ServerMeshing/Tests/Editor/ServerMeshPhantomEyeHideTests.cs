using System.Collections.Generic;
using System.Reflection;
using LiteNetLibManager;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomEyeHideTests
    {
        private const long Owner = 1_000_001;
        private static readonly PropertyInfo s_connectionId = typeof(LiteNetLibIdentity).GetProperty(nameof(LiteNetLibIdentity.ConnectionId));

        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            _objects.Clear();
        }

        private ServerMeshPhantomEye NewEye()
        {
            ServerMeshPhantomEye eye = ServerMeshPhantomEye.Create("phantom-eye-test", new Vector3(1f, 0f, 2f), HideFlags.HideAndDontSave);
            _objects.Add(eye.gameObject);
            s_connectionId.SetValue(eye.Identity, Owner);
            return eye;
        }

        private LiteNetLibIdentity NewObserver(long connectionId)
        {
            GameObject go = new GameObject("observer") { hideFlags = HideFlags.HideAndDontSave };
            _objects.Add(go);
            LiteNetLibIdentity identity = go.AddComponent<LiteNetLibIdentity>();
            s_connectionId.SetValue(identity, connectionId);
            return identity;
        }

        [Test]
        public void Eye_IsHiddenFromANonOwner()
        {
            ServerMeshPhantomEye eye = NewEye();

            Assert.That(eye.Identity.IsHide, Is.True);
            Assert.That(eye.Identity.IsHideFrom(NewObserver(7)), Is.True);
            Assert.That(eye.Identity.IsHideFrom(NewObserver(Owner + 1)), Is.True);
        }

        [Test]
        public void Eye_IsHiddenFromAnotherEye()
        {
            ServerMeshPhantomEye eye = NewEye();
            ServerMeshPhantomEye other = NewEye();
            s_connectionId.SetValue(other.Identity, Owner + 1);

            Assert.That(eye.Identity.IsHideFrom(other.Identity), Is.True);
            Assert.That(other.Identity.IsHideFrom(eye.Identity), Is.True);
        }

        [Test]
        public void Eye_IsVisibleToItsOwner()
        {
            ServerMeshPhantomEye eye = NewEye();

            Assert.That(eye.Identity.IsHideFrom(NewObserver(Owner)), Is.False);
        }

        [Test]
        public void Eye_LeavesHideExceptionsEmpty()
        {
            ServerMeshPhantomEye eye = NewEye();

            Assert.That(eye.Identity.HideExceptions, Is.Empty);
        }

        [Test]
        public void PlainIdentity_IsNotHidden()
        {
            LiteNetLibIdentity target = NewObserver(Owner);

            Assert.That(target.IsHide, Is.False);
            Assert.That(target.IsHideFrom(NewObserver(7)), Is.False);
        }

        [Test]
        public void Create_PlacesTheEyeAndItsIdentityTogether()
        {
            ServerMeshPhantomEye eye = NewEye();

            Assert.That(eye.transform.position, Is.EqualTo(new Vector3(1f, 0f, 2f)));
            Assert.That(eye.Identity, Is.SameAs(eye.GetComponent<LiteNetLibIdentity>()));
            Assert.That(eye.Host == null, Is.True);
        }

        [Test]
        public void Follow_MovesTheEyeOntoItsHost()
        {
            ServerMeshPhantomEye eye = NewEye();
            GameObject host = new GameObject("host") { hideFlags = HideFlags.HideAndDontSave };
            _objects.Add(host);
            host.transform.position = new Vector3(-30f, 0f, 12f);

            eye.Follow(host.transform);

            Assert.That(eye.transform.position, Is.EqualTo(host.transform.position));
            Assert.That(eye.Host, Is.SameAs(host.transform));
        }
    }
}
