using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public sealed class CSGStorage
{
    [Serializable]
    public class Entity
    {
        public int id;
        [SerializeReference] public CSGNode node;
        public Vector2 graphPosition;
        public bool hasGraphPosition;

        public Entity(int id, CSGNode node)
        {
            this.id = id;
            this.node = node;
        }
    }

    // Design choice: make m_entites private, such that add/delete cannot be affected by outside
    // Expose the list to outside through another public attribute
    [SerializeField] private List<Entity> m_entities = new List<Entity>();
    public IReadOnlyList<Entity> Entities => m_entities;
    [SerializeField] private int m_nextId = 1;

    public Entity Add(CSGNode node)
    {
        if (node == null)
            throw new ArgumentNullException(nameof(node));

        var entity = new Entity(m_nextId++, node);
        m_entities.Add(entity);
        return entity;
    }

    public bool Remove(CSGNode node)
    {
        if (node == null)
            throw new ArgumentNullException(nameof(node));
        
        // Foreach entity, compare their reference key
        int index = m_entities.FindIndex(entity => ReferenceEquals(entity.node, node));
        if (index < 0) // Required node does not exist, cannot remove
            return false;

        m_entities.RemoveAt(index);
        return true;
    }
}